// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Yarp.ReverseProxy.ServiceFabric
{
    /// <summary>
    /// Helper class to parse configuration labels of the gateway into actual objects.
    /// </summary>
    // TODO: this is probably something that can be used in other integration modules apart from Service Fabric. Consider extracting to a general class.
    internal static class LabelsParser
    {
        private static readonly Regex _allowedRouteNamesRegex = new Regex("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

        /// <summary>
        /// Requires all header match names to follow the .[0]. pattern to simulate indexing in an array
        /// </summary>
        private static readonly Regex _allowedHeaderNamesRegex = new Regex(@"^\[\d\d*\]$", RegexOptions.Compiled);


        /// Requires all transform names to follow the .[0]. pattern to simulate indexing in an array
        /// </summary>
        private static readonly Regex _allowedTransformNamesRegex = new Regex(@"^\[\d\d*\]$", RegexOptions.Compiled);

        // Look for route IDs
        private const string RoutesLabelsPrefix = "YARP.Routes.";

        internal static TValue GetLabel<TValue>(Dictionary<string, string> labels, string key, TValue defaultValue)
        {
            if (!labels.TryGetValue(key, out var value))
            {
                return defaultValue;
            }
            else
            {
                return ConvertLabelValue<TValue>(key, value);
            }
        }

        internal static TimeSpan? GetTimeSpanLabel(Dictionary<string, string> labels, string key, TimeSpan? defaultValue)
        {
            if (!labels.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
            {
                return defaultValue;
            }
            // Format "c" => [-][d'.']hh':'mm':'ss['.'fffffff]. 
            // You also can find more info at https://docs.microsoft.com/en-us/dotnet/standard/base-types/standard-timespan-format-strings#the-constant-c-format-specifier
            if (!TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var result))
            {
                throw new ConfigException($"Could not convert label {key}='{value}' to type TimeSpan. Use the format '[d.]hh:mm:ss'.");
            }
            return result;
        }

        private static TValue ConvertLabelValue<TValue>(string key, string value)
        {
            try
            {
                return (TValue)TypeDescriptor.GetConverter(typeof(TValue)).ConvertFromString(value);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException)
            {
                throw new ConfigException($"Could not convert label {key}='{value}' to type {typeof(TValue).FullName}.", ex);
            }
        }

        // TODO: optimize this method
        internal static List<RouteConfig> BuildRoutes(Uri serviceName, Dictionary<string, string> labels)
        {
            var backendId = GetClusterId(serviceName, labels);

            var routesNames = new Dictionary<StringSegment, string>();
            foreach (var kvp in labels)
            {
                if (kvp.Key.Length > RoutesLabelsPrefix.Length && kvp.Key.StartsWith(RoutesLabelsPrefix, StringComparison.Ordinal))
                {
                    var suffix = new StringSegment(kvp.Key).Subsegment(RoutesLabelsPrefix.Length);
                    var routeNameLength = suffix.IndexOf('.');
                    if (routeNameLength == -1)
                    {
                        // No route name encoded, the key is not valid. Throwing would suggest we actually check for all invalid keys, so just ignore.
                        continue;
                    }

                    var routeNameSegment = suffix.Subsegment(0, routeNameLength + 1);
                    if (routesNames.ContainsKey(routeNameSegment))
                    {
                        continue;
                    }

                    var routeName = routeNameSegment.Subsegment(0, routeNameSegment.Length - 1).ToString();
                    if (!_allowedRouteNamesRegex.IsMatch(routeName))
                    {
                        throw new ConfigException($"Invalid route name '{routeName}', should only contain alphanumerical characters, underscores or hyphens.");
                    }
                    routesNames.Add(routeNameSegment, routeName);
                }
            }

            // Build the routes
            var routes = new List<RouteConfig>(routesNames.Count);
            foreach (var routeNamePair in routesNames)
            {
                string hosts = null;
                string path = null;
                int? order = null;
                var metadata = new Dictionary<string, string>();
                var headerMatches = new Dictionary<string, RouteHeaderFields>();
                var transforms = new Dictionary<string, Dictionary<string, string>>();
                foreach (var kvp in labels)
                {
                    if (!kvp.Key.StartsWith(RoutesLabelsPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var routeLabelKey = kvp.Key.AsSpan().Slice(RoutesLabelsPrefix.Length);

                    if (routeLabelKey.Length < routeNamePair.Key.Length || !routeLabelKey.StartsWith(routeNamePair.Key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    routeLabelKey = routeLabelKey.Slice(routeNamePair.Key.Length);

                    if (ContainsKey("Metadata.", routeLabelKey, out var keyRemainder))
                    {
                        metadata.Add(keyRemainder.ToString(), kvp.Value);
                    }
                    else if (ContainsKey("MatchHeaders.", routeLabelKey, out keyRemainder))
                    {
                        var headerIndexLength = keyRemainder.IndexOf('.');
                        if (headerIndexLength == -1)
                        {
                            // No header encoded, the key is not valid. Throwing would suggest we actually check for all invalid keys, so just ignore.
                            continue;
                        }
                        var headerIndex = keyRemainder.Slice(0, headerIndexLength).ToString();
                        if (!_allowedHeaderNamesRegex.IsMatch(headerIndex))
                        {
                            throw new ConfigException($"Invalid header matching index '{headerIndex}', should only contain alphanumerical characters, underscores or hyphens.");
                        }
                        if (!headerMatches.ContainsKey(headerIndex))
                        {
                            headerMatches.Add(headerIndex, new RouteHeaderFields());
                        }

                        var propertyName = keyRemainder.Slice(headerIndexLength + 1);
                        if (propertyName.Equals("Name", StringComparison.Ordinal))
                        {
                            headerMatches[headerIndex].Name = kvp.Value;
                        }
                        else if (propertyName.Equals("Values", StringComparison.Ordinal))
                        {
#if NET
                            headerMatches[headerIndex].Values = kvp.Value.Split(',', StringSplitOptions.TrimEntries);
#elif NETCOREAPP3_1
                            headerMatches[headerIndex].Values = kvp.Value.Split(',').Select(val => val.Trim()).ToList();
#else
#error A target framework was added to the project and needs to be added to this condition.
#endif
                        }
                        else if (propertyName.Equals("IsCaseSensitive", StringComparison.Ordinal))
                        {
                            headerMatches[headerIndex].IsCaseSensitive = bool.Parse(kvp.Value);
                        }
                        else if (propertyName.Equals("Mode", StringComparison.Ordinal))
                        {
                            headerMatches[headerIndex].Mode = Enum.Parse<HeaderMatchMode>(kvp.Value);
                        }
                        else
                        {
                            throw new ConfigException($"Invalid header matching property '{propertyName.ToString()}', only valid values are Name, Values, IsCaseSensitive and Mode.");
                        }
                    }
                    else if (ContainsKey("Transforms.", routeLabelKey, out keyRemainder))
                    {
                        var transformNameLength = keyRemainder.IndexOf('.');
                        if (transformNameLength == -1)
                        {
                            // No transform index encoded, the key is not valid. Throwing would suggest we actually check for all invalid keys, so just ignore.
                            continue;
                        }
                        var transformName = keyRemainder.Slice(0, transformNameLength).ToString();
                        if (!_allowedTransformNamesRegex.IsMatch(transformName))
                        {
                            throw new ConfigException($"Invalid transform index '{transformName}', should be transform index wrapped in square brackets.");
                        }
                        if (!transforms.ContainsKey(transformName))
                        {
                            transforms.Add(transformName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                        }
                        var propertyName = keyRemainder.Slice(transformNameLength + 1).ToString();
                        if (!transforms[transformName].ContainsKey(propertyName))
                        {
                            transforms[transformName].Add(propertyName, kvp.Value);
                        }
                        else
                        {
                            throw new ConfigException($"A duplicate transformation property '{transformName}.{propertyName}' was found.");
                        }
                    }
                    else if (ContainsKey("Hosts", routeLabelKey, out _))
                    {
                        hosts = kvp.Value;
                    }
                    else if (ContainsKey("Path", routeLabelKey, out _))
                    {
                        path = kvp.Value;
                    }
                    else if (ContainsKey("Order", routeLabelKey, out _))
                    {
                        order = ConvertLabelValue<int?>(kvp.Key, kvp.Value);
                    }
                }

                var route = new RouteConfig
                {
                    RouteId = $"{Uri.EscapeDataString(backendId)}:{Uri.EscapeDataString(routeNamePair.Value)}",
                    Match = new RouteMatch
                    {
                        Hosts = SplitHosts(hosts),
                        Path = path,
                        Headers = headerMatches.Count > 0 ? headerMatches.Select(hm => new RouteHeader()
                        {
                            Name = hm.Value.Name,
                            Values = hm.Value.Values,
                            Mode = hm.Value.Mode,
                            IsCaseSensitive = hm.Value.IsCaseSensitive,
                        }).ToArray() : null

                    },
                    Order = order,
                    ClusterId = backendId,
                    Metadata = metadata,
                    Transforms = transforms.Count > 0 ? transforms.Select(tr => tr.Value).ToList().AsReadOnly() : null
                };
                routes.Add(route);
            }
            return routes;
        }

        internal static ClusterConfig BuildCluster(Uri serviceName, Dictionary<string, string> labels, IReadOnlyDictionary<string, DestinationConfig> destinations)
        {
            var clusterMetadata = new Dictionary<string, string>();
            const string BackendMetadataKeyPrefix = "YARP.Backend.Metadata.";
            foreach (var item in labels)
            {
                if (item.Key.StartsWith(BackendMetadataKeyPrefix, StringComparison.Ordinal))
                {
                    clusterMetadata[item.Key.Substring(BackendMetadataKeyPrefix.Length)] = item.Value;
                }
            }

            var clusterId = GetClusterId(serviceName, labels);

            var versionLabel = GetLabel<string>(labels, "YARP.Backend.HttpRequest.Version", null);

            var activityContextHeadersLabel = GetLabel<string>(labels, "YARP.Backend.HttpClient.ActivityContextHeaders", null);
            var sslProtocolsLabel = GetLabel<string>(labels, "YARP.Backend.HttpClient.SslProtocols", null);

#if NET
            var versionPolicyLabel = GetLabel<string>(labels, "YARP.Backend.HttpRequest.VersionPolicy", null);
#endif

            var cluster = new ClusterConfig
            {
                ClusterId = clusterId,
                LoadBalancingPolicy = GetLabel<string>(labels, "YARP.Backend.LoadBalancingPolicy", null),
                SessionAffinity = new SessionAffinityConfig
                {
                    Enabled = GetLabel<bool?>(labels, "YARP.Backend.SessionAffinity.Enabled", null),
                    Provider = GetLabel<string>(labels, "YARP.Backend.SessionAffinity.Provider", null),
                    FailurePolicy = GetLabel<string>(labels, "YARP.Backend.SessionAffinity.FailurePolicy", null),
                    AffinityKeyName = GetLabel<string>(labels, "YARP.Backend.SessionAffinity.AffinityKeyName", null),
                    Cookie = new SessionAffinityCookieConfig
                    {
                        Path = GetLabel<string>(labels, "YARP.Backend.SessionAffinity.Cookie.Path", null),
                        SameSite = GetLabel<SameSiteMode?>(labels, "YARP.Backend.SessionAffinity.Cookie.SameSite", null),
                        HttpOnly = GetLabel<bool?>(labels, "YARP.Backend.SessionAffinity.Cookie.HttpOnly", null),
                        MaxAge = GetTimeSpanLabel(labels, "YARP.Backend.SessionAffinity.Cookie.MaxAge", null),
                        Domain = GetLabel<string>(labels, "YARP.Backend.SessionAffinity.Cookie.Domain", null),
                        IsEssential = GetLabel<bool?>(labels, "YARP.Backend.SessionAffinity.Cookie.IsEssential", null),
                        SecurePolicy = GetLabel<CookieSecurePolicy?>(labels, "YARP.Backend.SessionAffinity.Cookie.SecurePolicy", null),
                        Expiration = GetTimeSpanLabel(labels, "YARP.Backend.SessionAffinity.Cookie.Expiration", null)
                    }
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    Timeout = GetTimeSpanLabel(labels, "YARP.Backend.HttpRequest.Timeout", null),
                    Version = !string.IsNullOrEmpty(versionLabel) ? Version.Parse(versionLabel + (versionLabel.Contains('.') ? "" : ".0")) : null,
                    AllowResponseBuffering = GetLabel<bool?>(labels, "YARP.Backend.HttpRequest.AllowResponseBuffering", null),
#if NET
                    VersionPolicy = !string.IsNullOrEmpty(versionLabel) ? Enum.Parse<HttpVersionPolicy>(versionPolicyLabel) : null
#endif
                },
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthCheckConfig
                    {
                        Enabled = GetLabel<bool?>(labels, "YARP.Backend.HealthCheck.Active.Enabled", null),
                        Interval = GetTimeSpanLabel(labels, "YARP.Backend.HealthCheck.Active.Interval", null),
                        Timeout = GetTimeSpanLabel(labels, "YARP.Backend.HealthCheck.Active.Timeout", null),
                        Path = GetLabel<string>(labels, "YARP.Backend.HealthCheck.Active.Path", null),
                        Policy = GetLabel<string>(labels, "YARP.Backend.HealthCheck.Active.Policy", null)
                    },
                    Passive = new PassiveHealthCheckConfig
                    {
                        Enabled = GetLabel<bool?>(labels, "YARP.Backend.HealthCheck.Passive.Enabled", null),
                        Policy = GetLabel<string>(labels, "YARP.Backend.HealthCheck.Passive.Policy", null),
                        ReactivationPeriod = GetTimeSpanLabel(labels, "YARP.Backend.HealthCheck.Passive.ReactivationPeriod", null)
                    },
                    AvailableDestinationsPolicy = GetLabel<string>(labels, "YARP.Backend.HealthCheck.AvailableDestinationsPolicy", null)
                },
                HttpClient = new HttpClientConfig
                {
                    DangerousAcceptAnyServerCertificate = GetLabel<bool?>(labels, "YARP.Backend.HttpClient.DangerousAcceptAnyServerCertificate", null),
                    MaxConnectionsPerServer = GetLabel<int?>(labels, "YARP.Backend.HttpClient.MaxConnectionsPerServer", null),
                    ActivityContextHeaders = !string.IsNullOrEmpty(activityContextHeadersLabel) ? Enum.Parse<ActivityContextHeaders>(activityContextHeadersLabel) : null,
                    SslProtocols = !string.IsNullOrEmpty(sslProtocolsLabel) ? Enum.Parse<SslProtocols>(sslProtocolsLabel) : null,
#if NET
                    EnableMultipleHttp2Connections = GetLabel<bool?>(labels, "YARP.Backend.HttpClient.EnableMultipleHttp2Connections", null),
                    RequestHeaderEncoding = GetLabel<string>(labels, "YARP.Backend.HttpClient.RequestHeaderEncoding", null),
#endif
                    WebProxy = new WebProxyConfig
                    {
                        Address = GetLabel<Uri>(labels, "YARP.Backend.HttpClient.WebProxy.Address", null),
                        BypassOnLocal = GetLabel<bool?>(labels, "YARP.Backend.HttpClient.WebProxy.BypassOnLocal", null),
                        UseDefaultCredentials = GetLabel<bool?>(labels, "YARP.Backend.HttpClient.WebProxy.UseDefaultCredentials", null),
                    }
                },
                Metadata = clusterMetadata,
                Destinations = destinations,
            };
            return cluster;
        }


        private static string GetClusterId(Uri serviceName, Dictionary<string, string> labels)
        {
            if (!labels.TryGetValue("YARP.Backend.BackendId", out var backendId) ||
                string.IsNullOrEmpty(backendId))
            {
                backendId = serviceName.ToString();
            }

            return backendId;
        }

        private static IReadOnlyList<string> SplitHosts(string hosts)
        {
            return hosts?.Split(',').Select(h => h.Trim()).Where(h => h.Length > 0).ToList();
        }

        private static bool ContainsKey(string expectedKeyName, ReadOnlySpan<char> actualKey, out ReadOnlySpan<char> keyRemainder)
        {
            keyRemainder = default;

            if (!actualKey.StartsWith(expectedKeyName, StringComparison.Ordinal))
            {
                return false;
            }

            keyRemainder = actualKey.Slice(expectedKeyName.Length);
            return true;
        }

        private class RouteHeaderFields
        {
            public string Name { get; internal set; }
            public IReadOnlyList<string> Values { get; internal set; }
            public bool IsCaseSensitive { get; internal set; }
            public HeaderMatchMode Mode { get; internal set; }
        }
    }
}
