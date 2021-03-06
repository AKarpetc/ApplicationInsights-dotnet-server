﻿namespace Microsoft.ApplicationInsights.DependencyCollector.Implementation.EventHandlers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using Microsoft.ApplicationInsights.Common;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;

    /// <summary>
    /// Implements EventHubs DiagnosticSource events handling.
    /// </summary>
    internal class EventHubsDiagnosticsEventHandler : DiagnosticsEventHandlerBase
    {
        public const string DiagnosticSourceName = "Microsoft.Azure.EventHubs";
        private const string EntityPropertyName = "Entity";
        private const string EndpointPropertyName = "Endpoint";

        internal EventHubsDiagnosticsEventHandler(TelemetryConfiguration configuration) : base(configuration)
        {
        }

        public override bool IsEventEnabled(string evnt, object arg1, object arg2)
        {
            return true;
        }

        public override void OnEvent(KeyValuePair<string, object> evnt, DiagnosticListener ignored)
        {
            Activity currentActivity = Activity.Current;

            switch (evnt.Key)
            {
                case "Microsoft.Azure.EventHubs.Send.Start":
                case "Microsoft.Azure.EventHubs.Receive.Start":

                    // As a first step in supporting W3C protocol in ApplicationInsights,
                    // we want to generate Activity Ids in the W3C compatible format.
                    // While .NET changes to Activity are pending, we want to ensure trace starts with W3C compatible Id
                    // as early as possible, so that everyone has a chance to upgrade and have compatibility with W3C systems once they arrive.
                    // So if there is no parent Activity (i.e. this request has happened in the background, without parent scope), we'll override 
                    // the current Activity with the one with properly formatted Id. This workaround should go away
                    // with W3C support on .NET https://github.com/dotnet/corefx/issues/30331 (TODO)
                    if (currentActivity.Parent == null && currentActivity.ParentId == null)
                    {
                        currentActivity.UpdateParent(StringUtilities.GenerateTraceId());
                    }

                    break;
                case "Microsoft.Azure.EventHubs.Send.Stop":
                case "Microsoft.Azure.EventHubs.Receive.Stop":
                    // If we started auxiliary Activity before to override the Id with W3C compatible one,
                    // now it's time to set end time on it
                    if (currentActivity.Duration == TimeSpan.Zero)
                    {
                        currentActivity.SetEndTime(DateTime.UtcNow);
                    }

                    this.OnDependency(evnt.Key, evnt.Value, currentActivity);
                    break;
            }
        }

        private void OnDependency(string name, object payload, Activity activity)
        {
            DependencyTelemetry telemetry = new DependencyTelemetry { Type = RemoteDependencyConstants.AzureEventHubs };

            this.SetCommonProperties(name, payload, activity, telemetry);

            // Endpoint is URL of particular EventHub, e.g. sb://eventhubname.servicebus.windows.net/
            string endpoint = this.FetchPayloadProperty<Uri>(name, EndpointPropertyName, payload)?.ToString();

            // Queue/Topic name, e.g. myqueue/mytopic
            string queueName = this.FetchPayloadProperty<string>(name, EntityPropertyName, payload);

            // Target uniquely identifies the resource, we use both: queueName and endpoint 
            // with schema used for SQL-dependencies
            telemetry.Target = string.Join(" | ", endpoint, queueName);

            this.TelemetryClient.TrackDependency(telemetry);
        }
    }
}
