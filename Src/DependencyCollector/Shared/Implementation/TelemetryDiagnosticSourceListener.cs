namespace Microsoft.ApplicationInsights.DependencyCollector.Implementation
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Web.Implementation;
    using Microsoft.ApplicationInsights.Channel;

    internal class TelemetryDiagnosticSourceListener : DiagnosticSourceListenerBase<HashSet<string>>
    {
        internal const string ActivityStartNameSuffix = ".Start";
        internal const string ActivityStopNameSuffix = ".Stop";

        private readonly HashSet<string> excludedDiagnosticSources 
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> excludedDiagnosticSourceActivities 
            = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);


        public TelemetryDiagnosticSourceListener(TelemetryConfiguration configuration, ICollection<string> excludeDiagnosticSourceActivities) 
            : base(configuration)
        {
            this.Client.Context.GetInternalContext().SdkVersion = SdkVersionUtils.GetSdkVersion("rdd" + RddSource.DiagnosticSourceListener + ":");
            this.PrepareExclusionLists(excludeDiagnosticSourceActivities);
        }

        internal override bool IsSourceEnabled(DiagnosticListener value)
        {
            return !this.excludedDiagnosticSources.Contains(value.Name);
        }

        protected override HashSet<string> GetListenerContext(DiagnosticListener diagnosticListener)
        {
            HashSet<string> excludedActivities;
            if (!this.excludedDiagnosticSourceActivities.TryGetValue(diagnosticListener.Name, out excludedActivities))
            {
                return null;
            }

            return excludedActivities;
        }

        internal override bool IsEventEnabled(string evnt, object input1, object input2, DiagnosticListener diagnosticListener, HashSet<string> context)
        {
            return !this.IsActivityExcluded(evnt, context)
                && !evnt.EndsWith(ActivityStartNameSuffix, StringComparison.OrdinalIgnoreCase);
        }

        internal override bool ShouldHandleEvent(KeyValuePair<string, object> evnt, DiagnosticListener diagnosticListener, HashSet<string> context)
        {
            return !this.IsActivityExcluded(evnt.Key, context)
                && evnt.Key.EndsWith(ActivityStopNameSuffix, StringComparison.OrdinalIgnoreCase);
        }

        internal bool IsActivityExcluded(string activityName, HashSet<string> excludedActivities)
        {
            return excludedActivities?.Contains(activityName) == true;
        }

        internal override void HandleEvent(KeyValuePair<string, object> evnt, DiagnosticListener diagnosticListener, HashSet<string> context)
        {
            Activity currentActivity = Activity.Current;
            if (currentActivity == null)
            {
                DependencyCollectorEventSource.Log.CurrentActivityIsNull();
                return;
            }

            DependencyCollectorEventSource.Log.TelemetryDiagnosticSourceListenerActivityStopped(currentActivity.Id, currentActivity.OperationName);

            // extensibility point - can chain more telemetry extraction methods here
            ITelemetry telemetry = this.ExtractDependencyTelemetry(diagnosticListener, currentActivity);
            if (telemetry == null)
            {
                return;
            }

            // properly fill dependency telemetry operation context
            telemetry.Context.Operation.Id = currentActivity.RootId;
            telemetry.Context.Operation.ParentId = currentActivity.ParentId;
            telemetry.Timestamp = currentActivity.StartTimeUtc;

            foreach (var item in currentActivity.Baggage)
            {
                if (!telemetry.Context.Properties.ContainsKey(item.Key))
                {
                    telemetry.Context.Properties[item.Key] = item.Value;
                }
            }

            telemetry.Context.Properties["DiagnosticSource"] = diagnosticListener.Name;
            telemetry.Context.Properties["Activity"] = currentActivity.OperationName;

            this.Client.Initialize(telemetry);

            this.Client.Track(telemetry);
        }

        internal DependencyTelemetry ExtractDependencyTelemetry(DiagnosticListener diagnosticListener, Activity currentActivity)
        {
            DependencyTelemetry telemetry = new DependencyTelemetry();

            telemetry.Id = currentActivity.Id;
            telemetry.Duration = currentActivity.Duration;

            Uri requestUri = null;
            string component = null;
            string dbStatement = null;
            string httpUrl = null;
            string peerAddress = null;
            string peerService = null;

            foreach (KeyValuePair<string, string> tag in currentActivity.Tags)
            {
                // interpret Tags as defined by OpenTracing conventions
                // https://github.com/opentracing/specification/blob/master/semantic_conventions.md
                switch (tag.Key)
                {
                    case "operation.name": // not defined by OpenTracing
                        {
                            telemetry.Name = tag.Value;
                            continue; // skip Properties
                        }
                    case "operation.type": // not defined by OpenTracing
                        {
                            telemetry.Type = tag.Value;
                            continue; // skip Properties
                        }
                    case "operation.data": // not defined by OpenTracing
                        {
                            telemetry.Data = tag.Value;
                            continue; // skip Properties
                        }
                    case "component":
                        {
                            component = tag.Value;
                            break;
                        }
                    case "db.statement":
                        {
                            dbStatement = tag.Value;
                            break;
                        }
                    case "error":
                        {
                            bool failed;
                            if (bool.TryParse(tag.Value, out failed))
                            {
                                telemetry.Success = !failed;
                                continue; // skip Properties
                            }
                            break;
                        }
                    case "http.status_code":
                        {
                            telemetry.ResultCode = tag.Value;
                            continue; // skip Properties
                        }
                    case "http.url":
                        {
                            httpUrl = tag.Value;
                            if (Uri.TryCreate(tag.Value, UriKind.RelativeOrAbsolute, out requestUri))
                            {
                                continue; // skip Properties
                            }
                            break;
                        }
                    case "peer.address":
                        {
                            peerAddress = tag.Value;
                            break;
                        }
                    case "peer.hostname":
                        {
                            telemetry.Target = tag.Value;
                            continue; // skip Properties
                        }
                    case "peer.service":
                        {
                            peerService = tag.Value;
                            break;
                        }
                }

                // if more than one tag with the same name is specified, the first one wins
                if (!telemetry.Properties.ContainsKey(tag.Key))
                {
                    telemetry.Properties[tag.Key] = tag.Value;
                }
            }

            if (string.IsNullOrEmpty(telemetry.Type))
            {
                telemetry.Type = peerService ?? component ?? diagnosticListener.Name;
            }

            if (string.IsNullOrEmpty(telemetry.Target))
            {
                // 'peer.address' can be not user-friendly, thus use only if nothing else specified
                telemetry.Target = requestUri?.Host ?? peerAddress;
            }

            if (string.IsNullOrEmpty(telemetry.Name))
            {
                telemetry.Name = currentActivity.OperationName;
            }

            if (string.IsNullOrEmpty(telemetry.Data))
            {
                telemetry.Data = dbStatement ?? requestUri?.OriginalString ?? httpUrl;
            }

            return telemetry;
        }

        private void PrepareExclusionLists(ICollection<string> excludeDiagnosticSourceActivities)
        {
            if (excludeDiagnosticSourceActivities == null)
            {
                return;
            }

            foreach (string exclusion in excludeDiagnosticSourceActivities)
            {
                if (string.IsNullOrWhiteSpace(exclusion))
                {
                    continue;
                }

                // each individual exclusion can specify
                // 1) the name of Diagnostic Source 
                //    - in that case the whole source is excluded
                //    - e.g. "System.Net.Http"
                // 2) the names of Diagnostic Source and Activity separated by ':' 
                //   - in that case the activity is disabled but not the whole source
                //   - e.g. ""
                string[] tokens = exclusion.Split(':');

                if (tokens.Length == 1)
                {
                    // the whole Diagnostic Source is excluded
                    this.excludedDiagnosticSources.Add(tokens[0]);
                }
                else
                {
                    // certain Activity from the Diagnostic Source is excluded
                    HashSet<string> excludedActivities;
                    if (!this.excludedDiagnosticSourceActivities.TryGetValue(tokens[0], out excludedActivities))
                    {
                        excludedActivities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        this.excludedDiagnosticSourceActivities[tokens[0]] = excludedActivities;
                    }

                    // exclude activity and activity Stop events
                    excludedActivities.Add(tokens[1]);
                    excludedActivities.Add(tokens[1] + ActivityStopNameSuffix);
                }
            }
        }
    }
}