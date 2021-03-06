﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using AttackSurfaceAnalyzer.Objects;
using AttackSurfaceAnalyzer.Utils;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Utf8Json;

namespace AttackSurfaceAnalyzer.Collectors
{
    /// <summary>
    /// Collects data from the local registry on Windows systems.
    /// </summary>
    public class RegistryCollector : BaseCollector
    {
        private readonly List<RegistryHive> Hives;
        private readonly HashSet<string> roots;
        private readonly HashSet<RegistryKey> _keys;
        private readonly HashSet<RegistryObject> _values;
        private readonly bool Parallelize;

        private static readonly ConcurrentDictionary<string, string> SidMap = new ConcurrentDictionary<string, string>();

        private static readonly List<RegistryHive> DefaultHives = new List<RegistryHive>()
        {
            RegistryHive.ClassesRoot, RegistryHive.CurrentConfig, RegistryHive.CurrentUser, RegistryHive.LocalMachine, RegistryHive.Users
        };

        private readonly Action<RegistryObject> customCrawlHandler = null;

        public RegistryCollector(string RunId, bool Parallelize) : this(RunId, DefaultHives, Parallelize, null) { }

        public RegistryCollector(string RunId, List<RegistryHive> Hives, bool Parallelize) : this(RunId, Hives, Parallelize, null) { }

        public RegistryCollector(string RunId, List<RegistryHive> Hives, bool Parallelize, Action<RegistryObject> customHandler)
        {
            this.RunId = RunId;
            this.Hives = Hives;
            roots = new HashSet<string>();
            _keys = new HashSet<RegistryKey>();
            _values = new HashSet<RegistryObject>();
            customCrawlHandler = customHandler;
            this.Parallelize = Parallelize;
        }

        public void AddRoot(string root)
        {
            roots.Add(root);
        }

        public void ClearRoots()
        {
            roots.Clear();
        }

        public override bool CanRunOnPlatform()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public static string GetName(RegistryAccessRule rule)
        {
            if (rule == null)
            {
                return string.Empty;
            }
            if (!SidMap.ContainsKey(rule.IdentityReference.Value))
            {
                try
                {
                    var mappedValue = rule.IdentityReference.Translate(typeof(NTAccount)).Value;
                    SidMap.TryAdd(rule.IdentityReference.Value, mappedValue);
                }
                catch (IdentityNotMappedException)
                {
                    // This is fine. Some SIDs don't map to NT Accounts.
                    SidMap.TryAdd(rule.IdentityReference.Value, rule.IdentityReference.Value);
                }
            }

            return SidMap[rule.IdentityReference.Value];
        }

        public override void ExecuteInternal()
        {
            foreach (var hive in Hives)
            {
                Log.Debug("Starting " + hive.ToString());
                if (!Filter.IsFiltered(AsaHelpers.GetPlatformString(), "Scan", "Registry", "Hive", "Include", hive.ToString()) && Filter.IsFiltered(AsaHelpers.GetPlatformString(), "Scan", "Registry", "Hive", "Exclude", hive.ToString(), out Regex Capturer))
                {
                    Log.Debug("{0} '{1}' {2} '{3}'.", Strings.Get("ExcludingHive"), hive.ToString(), Strings.Get("DueToFilter"), Capturer.ToString());
                    return;
                }

                Action<RegistryKey> IterateOn = registryKey =>
                {
                    try
                    {
                        var regObj = RegistryWalker.RegistryKeyToRegistryObject(registryKey);

                        if (regObj != null)
                        {
                            DatabaseManager.Write(regObj, RunId);
                        }
                    }
                    catch (InvalidOperationException e)
                    {
                        Log.Debug(e, JsonSerializer.Serialize(registryKey) + " invalid op exept");
                    }
                };

                Filter.IsFiltered(AsaHelpers.GetPlatformString(), "Scan", "Registry", "Key", "Exclude", hive.ToString());
                var registryInfoEnumerable = RegistryWalker.WalkHive(hive);

                if (Parallelize)
                {
                    Parallel.ForEach(registryInfoEnumerable,
                    (registryKey =>
                    {
                        IterateOn(registryKey);
                    }));
                }
                else
                {
                    foreach (var registryKey in registryInfoEnumerable)
                    {
                        IterateOn(registryKey);
                    }
                }
                Log.Debug("Finished " + hive.ToString());
            }
        }
    }
}