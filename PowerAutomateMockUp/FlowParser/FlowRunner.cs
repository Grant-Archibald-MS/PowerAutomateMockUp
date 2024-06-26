﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Parser.ExpressionParser;
using Parser.FlowParser.ActionExecutors;

namespace Parser.FlowParser
{
    public interface IFlowRunner
    {
        void InitializeFlowRunner(TextReader streamReader);
        void InitializeFlowRunner(in string path);
        Task<FlowReport> Trigger();
        Task<FlowReport> Trigger(ValueContainer triggerOutput);
    }

    public class FlowRunner : IFlowRunner
    {
        private readonly IState _state;
        private readonly FlowSettings _flowRunnerSettings;
        private readonly IScopeDepthManager _scopeManager;
        private readonly IActionExecutorFactory _actionExecutorFactory;
        private readonly ILogger<FlowRunner> _logger;
        private readonly IExpressionEngine _expressionEngine;
        private readonly Dictionary<string, ActionReport> _actionSates;
        private int _actionsExecuted;
        private JProperty _trigger;

        public FlowRunner(
            IState state,
            IScopeDepthManager scopeDepthManager,
            IOptions<FlowSettings> flowRunnerSettings,
            IActionExecutorFactory actionExecutorFactory,
            ILogger<FlowRunner> logger,
            IExpressionEngine expressionEngine)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _scopeManager = scopeDepthManager;
            _flowRunnerSettings = flowRunnerSettings?.Value;
            _actionExecutorFactory =
                actionExecutorFactory ?? throw new ArgumentNullException(nameof(actionExecutorFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _expressionEngine = expressionEngine ?? throw new ArgumentNullException(nameof(expressionEngine));
            _actionSates = new Dictionary<string, ActionReport>();
            _actionsExecuted = 0;
        }

        public void InitializeFlowRunner(in string path)
        {
            InitializeFlowRunner(new StreamReader(path));
        }

        public void InitializeFlowRunner(TextReader streamReader)
        {
            using var jsonTextReader = new JsonTextReader(streamReader);

            var flowJson = JToken.ReadFrom(jsonTextReader);
            var flowDefinition = flowJson.SelectToken("$.*.definition");
            _trigger = flowDefinition.SelectToken("$.triggers").First as JProperty;
            _scopeManager.Push("root", flowDefinition.SelectToken("$.actions").OfType<JProperty>(), null);
        }

        public async Task<FlowReport> Trigger()
        {
            var trigger = GetActionExecutor(_trigger);

            trigger.InitializeActionExecutor(_trigger.Name, _trigger.Value);
            var triggerResult = await trigger.Execute();

            if (triggerResult.ActionOutput != null)
            {
                _state.AddTriggerOutputs(triggerResult.ActionOutput);
            }

            await RunFlow();

            return new FlowReport
            {
                ActionStates = _actionSates,
                NumberOfExecutedActions = _actionsExecuted
            };
        }

        public async Task<FlowReport> Trigger(ValueContainer triggerOutput)
        {
            _state.AddTriggerOutputs(triggerOutput);

            await RunFlow();

            return new FlowReport
            {
                ActionStates = _actionSates,
                NumberOfExecutedActions = _actionsExecuted
            };
        }

        private async Task RunFlow()
        {
            var currentAd = _scopeManager.CurrentActionDescriptions.FirstOrDefault(
                ad => !ad.Value.SelectToken("$.runAfter").Any());

            while (currentAd != null)
            {
                if (_flowRunnerSettings.IgnoreActions.Contains(currentAd.Name))
                {
                    currentAd = _scopeManager.CurrentActionDescriptions.FirstOrDefault(a =>
                        a.Value.SelectToken("$.runAfter").First?.ToObject<JProperty>().Name == currentAd.Name
                        && a.Value.SelectToken("$.runAfter.*").Values().Any(
                            x => x?.Value<string>() == ActionStatus.Succeeded.ToString()));
                    continue;
                }

                var actionExecutor = GetActionExecutor(currentAd);

                var actionResult = await ExecuteAction(actionExecutor, currentAd);

                if (_flowRunnerSettings.LogActionsStates)
                {
                    var jsonInputs = currentAd.First?.SelectToken("$.inputs");

                    var called = 0;

                    if (!_actionSates.ContainsKey(currentAd.Name))
                    {
                        // Action state does not exist add to the state
                        _actionSates.Add(currentAd.Name, null);
                    }

                    var existing = _actionSates[currentAd.Name];
                    if ( existing != null)
                    {
                        called = existing.Called;
                    }

                    _actionSates[currentAd.Name] = new ActionReport
                    {
                        ActionJson = jsonInputs,
                        ActionInput = actionExecutor?.Inputs ??
                                      (jsonInputs == null ? null : new ValueContainer(jsonInputs, _expressionEngine)),
                        ActionOutput = actionResult,
                        ActionOrder = _actionsExecuted++,
                        ActionName = actionExecutor?.ActionName,
                        Called = called++
                    };
                }

                if (!(actionResult?.ContinueExecution ?? true))
                {
                    break;
                }

                // If an action fails inside a scope, and a suitable action isn't found inside the given scope, that 
                // actions status is transferred to be the scope status. This isn't the case atm

                var actionDescName = currentAd.Name;
                var nextAction = actionResult?.NextAction;
                var actionResultStatus = actionResult?.ActionStatus ?? ActionStatus.Succeeded;
                while (!DetermineNextAction(nextAction, actionResultStatus, out currentAd, actionDescName))
                {
                    nextAction = null;
                    var t = await _scopeManager.TryPopScope(actionResultStatus);
                    if (t == null)
                    {
                        currentAd = null;
                        break;
                    }

                    actionResultStatus = t.ActionStatus;
                    actionDescName = t.NextAction;
                }

                if (currentAd == null && actionResultStatus == ActionStatus.Failed)
                {
                    _logger.LogError(
                        "No succeeding action found after last action had status: Failed. Throwing error");
                    throw actionResult?.ActionExecutorException ??
                          new PowerAutomateMockUpException(
                              $"No exception recorded - {actionExecutor.ActionName} ended with status: Failed.");
                }
            }
        }

        private bool DetermineNextAction(string nextAction, ActionStatus actionResultStatus,
            out JProperty currentActionDesc, string adName)
        {
            if (nextAction == null)
            {
                currentActionDesc = _scopeManager.CurrentActionDescriptions.FirstOrDefault(a =>
                    a.Value.SelectToken("$.runAfter").First?.ToObject<JProperty>().Name == adName &&
                    a.Value.SelectToken("$.runAfter.*").Values().Any(
                        x => x?.Value<string>() == actionResultStatus.ToString()));
            }
            else
            {
                currentActionDesc =
                    _scopeManager.CurrentActionDescriptions.FirstOrDefault(a => a.Name == nextAction);
            }

            return currentActionDesc != null;
        }

        private async Task<ActionResult> ExecuteAction(ActionExecutorBase actionExecutor,
            JProperty currentAction)
        {
            if (actionExecutor == null) return null;

            actionExecutor.InitializeActionExecutor(currentAction.Name, currentAction.First);
            var executionResult = await actionExecutor.Execute();

            if (executionResult.ActionOutput != null)
            {
                _state.AddOutputs(actionExecutor.ActionName, executionResult.ActionOutput);
            }

            return executionResult;
        }

        private ActionExecutorBase GetActionExecutor(JProperty currentAction)
        {
            var actionTypeFromAd = currentAction.First.SelectToken("$.type");
            var actionType = actionTypeFromAd.Value<string>();

            var host = currentAction.First.SelectToken("$.inputs.host");

            var action = _actionExecutorFactory.ResolveActionByKey(currentAction.Name);

            var extraMessage = "";
            if (action == null && (actionType == "OpenApiConnection" || actionType == "OpenApiConnectionWebhook"))
            {
                var apiId = host.SelectToken("$.apiId").Value<string>();
                var operationId = host.SelectToken("$.operationId").Value<string>();
                action = _actionExecutorFactory.ResolveActionByApiId(apiId, operationId);
                extraMessage = $" , ApiId: {apiId} , OperationId: {operationId}";
            }

            action ??= _actionExecutorFactory.ResolveActionByType(actionTypeFromAd.Value<string>());

            if (action == null && _flowRunnerSettings.FailOnUnknownAction)
            {
                throw new Exception(
                    $"Could not find action to: {currentAction.Name} or by its type: {actionTypeFromAd}{extraMessage}. " +
                    "Register an Action either by Action Name or by its type in order to run this Flow."); // TODO: Create Exception
            }

            return action;
        }
    }
}