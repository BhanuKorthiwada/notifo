﻿// ==========================================================================
//  Notifo.io
// ==========================================================================
//  Copyright (c) Sebastian Stehle
//  All rights reserved. Licensed under the MIT license.
// ==========================================================================

using System.Globalization;
using Microsoft.Extensions.Logging;
using Notifo.Domain.Apps;
using Notifo.Domain.Resources;
using Notifo.Infrastructure;
using Notifo.Infrastructure.Mediator;
using Notifo.Infrastructure.Timers;
using Notifo.Infrastructure.Validation;
using Squidex.Hosting;

namespace Notifo.Domain.Integrations;

public sealed class IntegrationManager : IIntegrationManager, IBackgroundProcess
{
    private readonly IEnumerable<IIntegration> integrations;
    private readonly IAppStore appStore;
    private readonly IMediator mediator;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<IntegrationManager> log;
    private readonly ConditionEvaluator conditionEvaluator;
    private CompletionTimer timer;

    public IEnumerable<IntegrationDefinition> Definitions
    {
        get => integrations.Select(x => x.Definition);
    }

    public IntegrationManager(IEnumerable<IIntegration> integrations, IAppStore appStore, IMediator mediator,
        IServiceProvider serviceProvider, ILogger<IntegrationManager> log)
    {
        this.integrations = integrations;
        this.appStore = appStore;
        this.mediator = mediator;
        this.serviceProvider = serviceProvider;
        this.log = log;

        conditionEvaluator = new ConditionEvaluator(log);
    }

    public Task StartAsync(
        CancellationToken ct)
    {
        timer = new CompletionTimer(5000, CheckAsync, 5000);

        return Task.CompletedTask;
    }

    public Task StopAsync(
        CancellationToken ct)
    {
        return timer.StopAsync();
    }

    public Task ValidateAsync(ConfiguredIntegration configured,
        CancellationToken ct = default)
    {
        Guard.NotNull(configured);

        var integration = integrations.FirstOrDefault(x => x.Definition.Type == configured.Type);

        if (integration == null)
        {
            var error = string.Format(CultureInfo.InvariantCulture, Texts.IntegrationNotFound, configured.Type);

            throw new ValidationException(error);
        }

        var errors = new List<ValidationError>();

        foreach (var property in integration.Definition.Properties)
        {
            var value = configured.Properties.GetValueOrDefault(property.Name);

            foreach (var error in property.Validate(value))
            {
                errors.Add(new ValidationError(error, property.Name));
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return Task.CompletedTask;
    }

    public Task HandleConfiguredAsync(string id, App app, ConfiguredIntegration configured, ConfiguredIntegration? previous,
        CancellationToken ct = default)
    {
        Guard.NotNullOrEmpty(id);
        Guard.NotNull(app);
        Guard.NotNull(configured);

        var integration = integrations.FirstOrDefault(x => x.Definition.Type == configured.Type);

        if (integration == null)
        {
            var error = string.Format(CultureInfo.InvariantCulture, Texts.IntegrationNotFound, configured.Type);

            throw new ValidationException(error);
        }

        return integration.OnConfiguredAsync(app, id, configured, previous, ct);
    }

    public Task HandleRemovedAsync(string id, App app, ConfiguredIntegration configured,
        CancellationToken ct = default)
    {
        Guard.NotNullOrEmpty(id);
        Guard.NotNull(app);
        Guard.NotNull(configured);

        var integration = integrations.FirstOrDefault(x => x.Definition.Type == configured.Type);

        if (integration == null)
        {
            var error = string.Format(CultureInfo.InvariantCulture, Texts.IntegrationNotFound, configured.Type);

            throw new ValidationException(error);
        }

        return integration.OnRemovedAsync(app, id, configured, ct);
    }

    public bool IsConfigured<T>(App app, IIntegrationTarget? target = null)
    {
        Guard.NotNull(app);

        foreach (var (actualId, configured, integration) in GetIntegrations(app, target))
        {
            if (integration.CanCreate(typeof(T), actualId, configured))
            {
                return true;
            }
        }

        return false;
    }

    public T? Resolve<T>(string id, App app, IIntegrationTarget? target = null) where T : class
    {
        Guard.NotNullOrEmpty(id);
        Guard.NotNull(app);

        foreach (var (actualId, configured, integration) in GetIntegrations(app, target))
        {
            if (IsMatch(id, actualId) && integration.Create(typeof(T), actualId, configured, serviceProvider) is T created)
            {
                return created;
            }
        }

        return default;
    }

    public IEnumerable<(string, T)> Resolve<T>(App app, IIntegrationTarget? target = null) where T : class
    {
        Guard.NotNull(app);

        foreach (var (actualId, configured, integration) in GetIntegrations(app, target))
        {
            if (integration.Create(typeof(T), actualId, configured, serviceProvider) is T created)
            {
                yield return (actualId, created);
            }
        }

        yield break;
    }

    private IEnumerable<(string, ConfiguredIntegration, IIntegration)> GetIntegrations(App app, IIntegrationTarget? target)
    {
        var configureds = app.Integrations;

        foreach (var (actualId, configured) in configureds)
        {
            if (!IsReady(configured))
            {
                continue;
            }

            if (target != null && (!IsMatchingTest(configured, target) || !IsMatchingCondition(configured, target)))
            {
                continue;
            }

            var integration = integrations.FirstOrDefault(x => x.Definition.Type == configured.Type);

            if (integration != null)
            {
                yield return (actualId, configured, integration);
            }
        }
    }

    private static bool IsReady(ConfiguredIntegration configured)
    {
        return configured.Enabled && configured.Status == IntegrationStatus.Verified;
    }

    private static bool IsMatchingTest(ConfiguredIntegration configured, IIntegrationTarget target)
    {
        return configured.Test == null || configured.Test.Value == target.Test;
    }

    private bool IsMatchingCondition(ConfiguredIntegration configured, IIntegrationTarget target)
    {
        return conditionEvaluator.Evaluate(configured.Condition, target);
    }

    private static bool IsMatch(string id, string actualId)
    {
        return actualId == id;
    }

    public async Task CheckAsync(
        CancellationToken ct)
    {
        try
        {
            var apps = await appStore.QueryWithPendingIntegrationsAsync(ct);

            if (apps.Count == 0)
            {
                return;
            }

            foreach (var app in apps)
            {
                var updates = new Dictionary<string, IntegrationStatus>();

                foreach (var (id, configured) in app.Integrations)
                {
                    var status = configured.Status;

                    if (status != IntegrationStatus.Pending)
                    {
                        continue;
                    }

                    var integration = integrations.FirstOrDefault(x => x.Definition.Type == configured.Type);

                    if (integration == null)
                    {
                        updates[id] = IntegrationStatus.Verified;
                        continue;
                    }

                    try
                    {
                        await integration.CheckStatusAsync(app, id, configured, ct);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Check integrations failed.");
                    }

                    if (status != configured.Status)
                    {
                        updates[id] = IntegrationStatus.Verified;
                    }
                }

                if (updates.Count > 0)
                {
                    var command = new UpdateAppIntegrationStatus { AppId = app.Id, Status = updates };

                    await mediator.Send(command, ct);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Check integrations failed.");
        }
    }
}
