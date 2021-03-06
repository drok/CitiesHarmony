﻿/*
 * Harmony for Cities Skylines
 *  Copyright (C) 2021 Radu Hociung <radu.csmods@ohmi.org>
 *  
 *  This program is free software; you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation; either version 2 of the License, or
 *  (at your option) any later version.
 *  
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *  
 *  You should have received a copy of the GNU General Public License along
 *  with this program; if not, write to the Free Software Foundation, Inc.,
 *  51 Franklin Street, Fifth Floor, Boston, MA 02110-1301 USA.
 */

extern alias Harmony2;
extern alias Harmony2009;
extern alias Harmony2010;
extern alias HarmonyCHH2040;
using JetBrains.Annotations;
using IAwareness;
using ICities;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using ColossalFramework.PlatformServices;
using static ColossalFramework.Plugins.PluginManager;
using System.Collections.Generic;
using UnityEngine.Assertions;
using Harmony2::HarmonyLib;

namespace HarmonyMod
{


    public class Mod : IUserMod, IAmAware, ILoadingExtension
    {

#if DEVELOPER
        internal const string RECOMMENDED_LOCAL_HELPER_DIRNAME = "000-" + Versioning.PACKAGE_NAME + "-HELPER";
#endif
#if TRACE
        internal static int instancenum = 0;
#endif

        #region IUserMod Name and Description
        public string Name {
            get {
                bool firstListing = (firstRun && handover != null && handover.isMainMod) ||
                    (!firstRun && report != null && report.numSelfProblems != 0)
#if DEVELOPER
                    ||
                    (!firstRun && handover != null && handover.isMainMod && handover.helperMod == null && !handover.isFirst) ||
                    (!firstRun && handover != null && handover.isHelper && !handover.isFirst)
#endif
                    ;

                /* FIXME: Find a more elegant way to call isEnabled=true
                 * Cannot be called from the static constructor because it runs inside get_userModInstance()
                 */
                if (needAutoEnableCall)
                {
                    needAutoEnableCall = false;
                    AutoEnableOnce(false);
                }
                return
#if DEVELOPER_UPDATER || DEVELOPER
                    (firstListing ? " " : string.Empty) + "Diagnostics for Harmony" +
#else
                    (firstListing ? " Harmony" : "Harmony") +
#endif
                    (handover != null && handover.isHelper ? " HELPER" : "")
#if !RELEASE
                    + " " + Versioning.MyInformationalVersion
#endif
                 ;
            }
        }

        public string Description {
            get
            {
                /* If the mod is disabled, handover is null, otherwise handover is valid. */
                bool firstListing = (report != null && report.numSelfProblems != 0)
#if DEVELOPER
                    ||
                    (handover != null && handover.isMainMod && handover.helperMod == null && !handover.isFirst) ||
                    (handover != null && handover.isHelper && !handover.isFirst)
#endif
                    ;

                string mode = (handover == null) ? string.Empty :
                            handover.isMainMod || handover.isHelper ? "active" :
                            "standby";

                string myLocation = (handover == null) ? string.Empty :
                    ((handover.isLocal ? " (local '" : " (workshop '") + handover.self.name + "', " + mode + ")");

                return (firstRun && handover != null && handover.isMainMod) ?
#if DEVELOPER
                    $"Welcome to Harmony. Accurate Error Reporting begins after restart, but it can wait. {myLocation}" :

#else
                    $"Welcome to Harmony. Auto-enabled to compile the '{Report.REPORT_TITLE}'. Enjoy." :
#endif
#if DEVELOPER_UPDATER
                    firstListing ? $"Updater for the local Diagnostics mod{myLocation} (FAILURE, see '{Report.REPORT_TITLE}')" : $"Updater for the local Diagnostics mod{myLocation}";
#elif DEVELOPER
                    firstListing ? $"Early exception handler and mod analyzer{myLocation} (FAILURE, see '{Report.REPORT_TITLE}')" : $"Early exception handler and mod analyzer{myLocation}";
#else
                    firstListing ? $"Mod Dependency {myLocation} (FAILURE, see '{Report.REPORT_TITLE}')" : $"Mod Dependency{myLocation}";
#endif
            }
        }
        #endregion

        /* enabled is sticky, as this mod can only be enabled for now, but not disabled.
         * FIXME: Implement teardown, then disabling will work.
         */
        /*
         * FIXME: For bonus points, Helper should automatically become main when main is deleted live.
         * This would mean main calling helper from OnDisable(). Need an API so different vesions can talk.
         */
        bool enabled;
        internal static bool firstRun = false;
        internal static PluginInfo mainMod;
        internal static PluginInfo helperMod;
        internal static Mod mainModInstance;

        internal bool isHelperFirst => handover.isHelperFirst;
        internal bool isFirst => handover.isFirst;
#if DEVELOPER
        internal bool isLocal => handover.isLocal;
        internal bool isHelperLocal => handover.isHelperLocal;
        bool haveOwnHarmonyLib = false;
#endif

        internal Report report;

        static bool needAutoEnableCall = false;
#if DEVELOPER_UPDATER
        static bool needInstallCall = true;
#endif

        Handover handover;
        Patcher patcher;
        bool pluginManagerShuttingDown = false;

        static Mod()
        {
            try
            {
                //#if DEBUG
                /* Workaround for https://github.com/drok/Harmony-CitiesSkylines/issues/1
                 */
                var a = Assembly.GetExecutingAssembly();
                mainMod = Singleton<PluginManager>.instance.GetPluginsInfo().FirstOrDefault((x) => x.ContainsAssembly(a));
                if (mainMod != null)
                {
                    if (!mainMod.isEnabled)
                    {
                        var stackTrace = new System.Diagnostics.StackTrace(0, false);
                        var caller = stackTrace.GetFrame(stackTrace.FrameCount - 1).GetMethod();
                        var callerName = caller.DeclaringType.FullName + "." + caller.Name;
#if TRACE
                        UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod..cctor() thread={Thread.CurrentThread.ManagedThreadId} plugin={mainMod != null} initiated by {callerName}\n{(new System.Diagnostics.StackTrace(0, true)).ToString()}");
#endif
                        if (callerName == "MenuPanel.Start")
                        {
                            /* Need to call EnableOnce after the constructor returns */
                            needAutoEnableCall = true;
                        }
                        else /* assume call from Starter.Awake() */
                        /* When instantiated via Starter, it is likely another mod scanning the mod list,
                         * instantiating all, including disabled mods, and not the Starter instantiating
                         * because this mod is actually enabled.
                         *
                         * This logic of examining the stack trace is needed because of these scanning mods
                         * Without them, it would be simply
                         * if (mainMod != null && !mainMod.isEnabled) {
                         *      ActiveAutoEnableOnce();
                         * }
                         *
                         * However, because of them, the instantiation happens inside the PluginManager.AddPlugins()
                         * loop, which means mod.IsEnabled=true cannot be called, but must be scheduled for later.
                         * mod.IsEnabled=true would cause a second instance to be instantiated first and OnEnabled(),
                         * but the first instance added as userModInstance, not OnEnabled(), but present
                         * on the instance list.
                         */
                        {
                            /* I've already been added to the plugin list. Need active enable */
                            Singleton<PluginManager>.instance.eventPluginsChanged += ActiveAutoEnableOnce;
                        }
                    }
#if DEVELOPER_UPDATER
                    AutoInstallHelperOnce(mainMod);
                    needInstallCall = false;
#endif
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] Mod..cctor: ({Report.ExMessage(ex, true)})");
            }
#if HEAVY_TRACE
            UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] Mod..cctor DONE");
#endif
        }
        // Install Harmony as soon as possible to avoid problems with mods not following the guidelines

#if TRACE
        public Mod()
        {
            ++instancenum;
            UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod..ctor() thread={Thread.CurrentThread.ManagedThreadId} plugin={mainMod != null}\n{(new System.Diagnostics.StackTrace(0, true)).ToString()}");
        }
#endif

#region UserMod invoked handlers
        public void OnEnabled()
        {
            try
            {
#if DEBUG
                ListLoadedMods();
#endif
                if (handover == null)
                {
                    handover = new Handover(this);
                }

#if TRACE
                UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] Mod.OnEnabled() I am={handover.self.name} #{instancenum}");
#endif

                if (handover.BootStrapMainMod())
                {
                    if (handover.isHelper)
                    {
                        helperMod = handover.helperMod;
                    }
                    var mainModName = $"{(handover.mainMod().userModInstance as IUserMod).Name} {handover.mainModVersion}";
#if TRACE
                    UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: Helper, main Mod '{mainModName}' was bootstrapped.");
#endif
                }
                else
                {
#if TRACE
                    UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: I am the active Harmony.");
#endif

                    OnActive();

#if DEVELOPER
                    if (!handover.isFirst && !handover.isHelperFirst)
                    {
                        UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] INFO - reporting - SelfProblemType.NotLoadedFirst on SELF");
                        SelfReport(SelfProblemType.NotLoadedFirst);
                    }

#if DEVELOPER_UPDATER
                    /* Update helper if I (main) am not first.
                     * Write its DLL with my _helper_dll file, which is a higher version of myself.
                     * 
                     * Also when the workshop mod is updated, the local helper's DLL will be updated to the
                     * newly released _helper_dll.
                     * 
                     * The _helper_dll file is the same as the main DLL, but with version incremented by 1
                     * This allows the game to recongnize the helper and the workshop mod as distinct mods.
                     * If they are identical, the game only loads the workshop (late).
                     * 
                     * On the next game restart, the helper will be main and remain main.
                     * If the file copy fails, the local helper will handover to the more recent
                     * main mod from workshop.
                     */
                    if (needInstallCall)
                    {
                        if (helperMod != null && helperMod.userModInstance.GetType().Assembly.GetName().Version < Assembly.GetExecutingAssembly().GetName().Version)
                        {
                            needInstallCall = false;
                            AutoInstallHelperOnce(mainMod);
                        //HelperUpdate(handover.mainMod());
                        }
                    }
#endif
#endif
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] ERROR FAIL TO INIT:\n{Report.ExMessage(ex, true)} ... \n {ex.StackTrace}\n\ncall from {new System.Diagnostics.StackTrace(true)}");
                SelfReport(SelfProblemType.FailedToInitialize, ex);
            }
        }

        public void OnDisabled()
        {
            try
            {

                /* Check handover, because OnDisable can come before OnEnable,
                 * eg, on 1st install, when the workshop copy installs a local copy,
                 * the local copy is enabled asynchronously and does its handover,
                 * instantiating the workshop copy. Then the PluginManager
                 * unloads this early instance before installing it itself.
                 */

                if (handover != null)
                {
#if TRACE
                    UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] Mod.OnDisabled() I am={handover.self.name} #{instancenum} " +
                        $"{(pluginManagerShuttingDown ? "Exiting Application" : "Disabled by user action")}");
#endif
                    if (enabled)
                    {
#if HEAVY_TRACE
                        resolver?.Uninstall();
#endif
                        if (!pluginManagerShuttingDown) /* If shutdown is user requested, do it. */
                            patcher?.Uninstall();
                        /* The last Harmony Mod turn off the HarmonyLib */
#if IMPLEMENTED_ORDERLY_APP_SHUTDOWN
                        if (handover.mainMod() == null)
                        {
                            Deactivate();
                        }
#endif
                    }

                    if (handover.isMainMod && helperMod != null)
                    {
                        handover.NotifyStandbys(false);
                    }

#if DEBUG
                    ListLoadedMods();
#endif
                    if (!pluginManagerShuttingDown)
                    {
                        report?.OnDisabled(handover.self, false);
                        report = null;
                    }
                    enabled = false;
                }
#if HEAVY_TRACE
                else
                {
                    UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] Mod.OnDisabled() #{instancenum}");
                }
#endif

            }
            catch (Exception ex)
            {
                SelfReport(SelfProblemType.FailedToUninstall, ex);
            }
        }
#endregion

#region LoadingExtension Handlers
        [UsedImplicitly]
        void ILoadingExtension.OnCreated(ILoading loading)
        {
#if TRACE
            UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] INFO: Mod.OnCreated(mode={loading.currentMode}, complete={loading.loadingComplete}");
#endif
            if (loading.loadingComplete == false)
            {
#if BETA || DEVELOPER
                report?.OutputReport(this, false, $"Mods Loaded for {loading.currentMode} mode");
#endif
            } else
            {
                /* One mod was removed or reloaded */
                report?.OnModListChanged(loading.currentMode == AppMode.Game);
            }
        }

        [UsedImplicitly]
        void ILoadingExtension.OnReleased()
        {
#if TRACE
            /* can't report here, already disabled */
            UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] INFO: Mod.OnReleased()");
#endif
#if BETA || DEVELOPER
            report?.OutputReport(this, false, "Mods Unloaded");
#endif
        }

        [UsedImplicitly]
        void ILoadingExtension.OnLevelLoaded(LoadMode mode)
        {

            var meta = Singleton<SimulationManager>.instance.m_metaData;
            string brokenAssets = string.IsNullOrEmpty(Singleton<LoadingManager>.instance.m_brokenAssets) ? " (with broken assets)" : string.Empty;
            string fileID = meta.m_WorkshopPublishedFileId != PublishedFileId.invalid ? $" - {meta.m_WorkshopPublishedFileId}" : string.Empty;

            report?.ReportActivity($"Level Loaded {mode} - {meta.m_CityName} " +
                $"({meta.m_MapName}{fileID}, ) at " +
                $"{meta.m_currentDateTime}" +
                $"{brokenAssets}");
            report?.OutputReport(this, false, "Level Loaded");
        }

        [UsedImplicitly]
        void ILoadingExtension.OnLevelUnloading()
        {
#if BETA || DEVELOPER
            var meta = Singleton<SimulationManager>.instance.m_metaData;
            string fileID = meta.m_WorkshopPublishedFileId != PublishedFileId.invalid ? $" - {meta.m_WorkshopPublishedFileId}" : string.Empty;
            string brokenAssets = string.IsNullOrEmpty(Singleton<LoadingManager>.instance.m_brokenAssets) ? " (with broken assets)" : string.Empty;

            report?.ReportActivity($"Level Unloading - {meta.m_CityName} " +
                $"({meta.m_MapName}{fileID}) at " +
                $"{meta.m_currentDateTime}" +
                $"{brokenAssets}");
            report?.OutputReport(this, false, "Level Unloaded");
#endif
        }
#endregion

        internal void OnPluginManagerDestroyStart()
        {
            pluginManagerShuttingDown = true;
        }
        internal void OnPluginManagerDestroyDone()
        {
            patcher?.Uninstall();
            patcher = null;

            report?.OnDisabled(mainMod, false);
            report = null;
        }

        void OnActive()
        {
            mainMod = handover.mainMod();
            mainModInstance = mainMod.userModInstance as Mod;
            helperMod = handover.helperMod;

            bool haveHarmony = true;
            /* I am the main mod */
            if (!enabled)
            {
                enabled = true;
                if (report == null)
                {
                    report = new Report();
                    if (Harmony.unsupportedException != null)
                    {
                        report.ReportUnsupportedHarmony(Harmony.unsupportedException as HarmonyModSupportException);
                    }
                }
                if (haveHarmony)
                {
                    patcher = new Patcher(this, handover.self.name);
                    patcher.Install();
                }

#if DEVELOPER
                if (helperMod != null && !handover.isHelperFirst)
                {
                    UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] INFO - reporting - ModReport.ProblemType.HelperNotLoadedFirst on helper");
                    if (!firstRun) 
                        report.ReportPlugin(helperMod, ModReport.ProblemType.HelperNotLoadedFirst);
                }
#endif
                report.OnEnabled();
            }

            handover.NotifyStandbys(true);
        }

        void Deactivate()
        {
#if DEVELOPER
            /* FIXME: Implement API to provide status to other mods */
            patcher?.UninstallAll();
            if (haveOwnHarmonyLib)
            {
                Harmony.isEnabled = false;
            }
#endif
        }

#region Awareness Handlers
        /* FIXME: If the app is shutting down, I have to be last to disable,
         * to catch exceptions from other mods disabling, and to turn off HarmonyLib(s) last.
         */
        // void IAwareness.IAmAware.OnMainModChanged(IAwareness0::IAwareness.IAmAware main, bool enabled)
        void IAmAware.OnMainModChanged(IAmAware main, bool enabled)
        {
            try
            {

                if (handover != null)
                {

#if TRACE
                    UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod.OnMainModChanged() enabled={enabled} I am={handover.self.name}");
#endif

                    /* TODO: Implement */
                    if (handover.BootStrapMainMod(enabled))
                    {
                        var mainModName = $"{(handover.mainMod().userModInstance as IUserMod).Name} {handover.mainModVersion}";
                        if (handover.isHelper)
                        {
                            helperMod = handover.helperMod;
                            mainMod = handover.mainMod();
                            mainModInstance = mainMod.userModInstance as Mod;

                            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: I switched to Helper role to Mod '{mainModName}'");
                        } else
                        {
                            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: I switched to Standby role to Mod '{mainModName}'");
                        }


                        if (enabled)
                        {
#if HEAVY_TRACE
                            resolver.Uninstall();
#endif
                            patcher.Uninstall();
                            main.PutModReports(report.modReports);
                            report.OnDisabled(handover.self, true);
                            report = null;
                        }
                    } else
                    {
#if DEBUG
                        var oldMain = Singleton<PluginManager>.instance.GetPluginsInfo().First((x) => x.isEnabledNoOverride && x.userModInstance == main);
                        UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: I switched to Main role from {oldMain.name}");
#endif
                        OnActive();
                    }

                    Assert.IsTrue(mainMod.userModInstance is IAmAware,
                        "Only IAmAware instances can be main mods");
                    Assert.IsFalse(enabled && handover.isMainMod,
                        "Notified instance should concur that it is no longer the main Mod");
                    Assert.IsTrue(main == handover.mainMod(),
                        "Notification of another Harmony mod being main should be accurate");
                }
#if HEAVY_TRACE
                else
                {
                    UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod.OnMainModChanged() enabled={enabled}");
                }
#endif
            }
            catch (Exception ex)
            {
                SelfReport(SelfProblemType.FailedToYieldToMain, ex);
            }

        }

        //public void PutModReports(Dictionary<PluginInfo, IAwareness0::IAwareness.ModReportBase> reports)
        void IAmAware.PutModReports(Dictionary<PluginInfo, ModReportBase> reports)
        {
#if TRACE
            UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod.PutModReports() enabled={enabled} I am={handover.self.name}");
#endif
            try
            {


                if (report != null)
                {
                    report.PutModReports(reports);
                }
            }
            catch (Exception ex)
            {
                SelfReport(SelfProblemType.FailedToReceiveReports, ex);
            }
        }
        void IAmAware.SelfReport(SelfProblemType problem, Exception e)
        {

            string debind = $"{(mainMod!=null?"m":"-")}{((mainMod?.userModInstance as Mod) != null ? "M" : "-")}" +
                $"{(report != null ? "R":"-")}";
            if (e != null)
            {
                UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] ERROR (s{debind}) - {Report.SelfProblemText(problem)}: {Report.ExMessage(e, true, 1)}");
            }
            else
            {
                UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] ERROR (se{debind}) - {Report.SelfProblemText(problem)}");
            }
            if (mainMod != null && report != null)
            {
                (mainMod.userModInstance as Mod).report.ReportSelf(problem, e);
            }
        }
        bool IAmAware.IsFullyAware()
        {
#if DEVELOPER
            var oneShotAutoInstallHelper = new SavedBool(name: $"{Versioning.PACKAGE_NAME}.helper-install",
                fileName: Settings.userGameState,
                def: false,
                autoUpdate: true);
            return oneShotAutoInstallHelper && !firstRun;
#else
            return !firstRun;
#endif
        }

        /* Switchover from Harmony1 -> Harmony2
         * 
         * When Harmony2 is instantiated before being explicitly enabled,
         * it calls this callback.
         * Any previously applied Harmony1 patches are moved to Harmony2
         * and the existing Harmony1 assemblies are patched to redirect
         * to Harmony2
         * 
         * This should only be called once when HarmonyMod is first subscribed.
         * Afterwards it runs first and patches the H1 assemblies before
         * they have a chance to run, so no state transfer is needed.
         * 
         * But, if the "start first" logic should break, this state transfer
         * will be called when needed (before 1st H2 access)
         */
        void IAmAware.OnHarmonyAccessBeforeAwareness(bool needHarmon1StateTransfer)
        {
#if TRACE
            var origin = Report.FindCallOrigin(new System.Diagnostics.StackTrace(1, false));
            string origin_string = (origin != null && origin.isEnabledNoOverride && origin.userModInstance != null) ? $" on behalf of '{(origin.userModInstance as IUserMod).Name}'" : string.Empty;
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] OnHarmonyAccessBeforeAwareness({needHarmon1StateTransfer}){origin_string}");
#endif

            if (!Harmony.Harmony1Patched)
            {
                Harmony.Harmony1Patched = true;
                new Patcher(this, "Compatibility", true).ImplementAdditionalVersionSupport(needHarmon1StateTransfer);
            }
        }

        bool IAmAware.DoOnHarmonyReady(List<ClientCallback> callbacks)
        {
            callbacks.Do((c) => c.action());

            return true;
        }

        void IAmAware.CancelHarmonyReadyCallback(List<ClientCallback> callbacks)
        {
        }

        void ProhibitPatchingHarmony(MethodBase original, MethodBase caller)
        {
            SameAssemblyName sameAssembly = new SameAssemblyName();
            var myAssembly = Assembly.GetExecutingAssembly();
            if (sameAssembly.Equals(caller.DeclaringType.Assembly.GetName(), myAssembly.GetName()))
            {
                return;
            }
            var originalAssemblyName = original.DeclaringType.Assembly.GetName();
            SameAssemblyName signatureComparer = new SameAssemblyName(false, true, false, false);

            if (signatureComparer.Equals(originalAssemblyName, myAssembly.GetName()) || originalAssemblyName.Name == "0Harmony")
            {
                throw new HarmonyModACLException($"(Un)Patching {Versioning.PACKAGE_NAME} is prohibited");
            }
        }

        void ProhibitRemovingHarmony(MethodBase orginal, MethodBase caller, MethodInfo patchMethod)
        {
            SameAssemblyName sameAssembly = new SameAssemblyName();
            var myAssembly = Assembly.GetExecutingAssembly();
            if (sameAssembly.Equals(patchMethod.DeclaringType.Assembly.GetName(), myAssembly.GetName()) &&
                !sameAssembly.Equals(caller.DeclaringType.Assembly.GetName(), myAssembly.GetName()))
            {
                throw new HarmonyModACLException($"(Un)Patching {Versioning.PACKAGE_NAME} is prohibited");
            }
#if HEAVY_TRACE
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] UnpatchACL allows because caller is self or patch is not mine.");
#endif
        }

        bool IAmAware.PatchACL(MethodBase original, MethodBase caller, object patchMethod, Enum patchType)
        {

            if (original == null)
            {
                if (!Harmony.harmonyUsers.TryGetValue(caller.DeclaringType.Assembly.FullName, out var userStatus))
                {
                    var callerAssembly = caller.DeclaringType.Assembly.GetName();

                    var ex = Report.GetAPIMisuseException(callerAssembly, out var reason);
                    if (Versioning.IsObsolete(Versioning.Obsolescence.PROHIBIT_API_MISUSE_AFTER, reason))
                    {
                        throw ex;
                    }

                    Harmony.harmonyUsers[caller.DeclaringType.Assembly.FullName] = new Harmony.HarmonyUser()
                        { instancesCreated = 1, checkBeforeUse = false, legacyCaller = false};
                }
                else
                {
                    userStatus.instancesCreated++;
                }
            } else
            {
                ProhibitPatchingHarmony(original, caller);
#if HEAVY_TRACE
            string MethodInfoFullName(MethodInfo m)
            {
                if (m == null) return "m-is-null";
                return m.DeclaringType?.FullName + "." + m.Name;
            }

            var patchMethodName =
                (patchMethod is null) ? "*null*" :
                (patchMethod is HarmonyMethod) ? MethodInfoFullName((patchMethod as HarmonyMethod).method) :
                (patchMethod is Harmony2009::HarmonyLib.HarmonyMethod) ? MethodInfoFullName((patchMethod as Harmony2009::HarmonyLib.HarmonyMethod).method) :
                (patchMethod is Harmony2010::HarmonyLib.HarmonyMethod) ? MethodInfoFullName((patchMethod as Harmony2010::HarmonyLib.HarmonyMethod).method) :
                (patchMethod is HarmonyCHH2040::HarmonyLib.HarmonyMethod) ? MethodInfoFullName((patchMethod as HarmonyCHH2040::HarmonyLib.HarmonyMethod).method) :
                patchMethod.GetType().FullName;

            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] PatchACL allows {caller.DeclaringType.FullName}.{caller.Name} to patch {original.DeclaringType.FullName}.{original.Name} with {patchMethodName} as {patchType}");
#endif
            }

            return true;
        }

        bool IAmAware.UnpatchACL(MethodBase original, MethodBase caller, MethodInfo patchMethod)
        {
            // ProhibitPatchingHarmony(original, caller);

            ProhibitRemovingHarmony(original, caller, patchMethod);

#if HEAVY_TRACE
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] UnpatchACL allows {caller.DeclaringType.FullName}.{caller.Name} to unpatch {original.DeclaringType.FullName}.{original.Name}");
#endif

            return true;
        }
#endregion


#if DEBUG
        private void ListLoadedMods()
        {
            return;
            /* mod.userModInstance is null is some cases?, causing this to segfault */



            string mods = string.Empty;
            foreach (PluginInfo mod in Singleton<PluginManager>.instance.GetPluginsInfo())
            {
                if (mod.assemblyCount != 0)
                {
                    var refs = mod.userModInstance.GetType().Assembly.GetReferencedAssemblies();
                    mods += $"{mod.name} - @ {mod.modPath} - enabled={mod.isEnabled} - {mod.assembliesString} - {refs.Length} refs:\n";
                    foreach (var r in refs)
                    {
                        mods += $"       {r.FullName}\n";
                    }
                } else
                {
                    mods += $"{mod.name} - @ {mod.modPath} - enabled={mod.isEnabled} - {mod.assembliesString} - UNLOADED\n";
                }
            }
#if DEBUG
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] The following mods are instantiated:\n{mods}");
#endif

            mods = string.Empty;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach(var a in assemblies)
            {
                mods += $"    {a.GetName().FullName} - GAC={a.GlobalAssemblyCache} reflectionOnly={a.ReflectionOnly}\n";
            }
#if DEBUG
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] The following assemblies are currently loaded:\n{mods}");
#endif


        }
#endif

#region Helper Maintenance
        static void ActiveAutoEnableOnce()
        {
            AutoEnableOnce(true);
        }
        static void ActiveAutoEnableOnce_Scheduled()
        {
            AutoEnableOnce(false);
        }

        static void AutoEnableOnce(bool deferred)
        {
            
            if (deferred)
            {
                Singleton<PluginManager>.instance.eventPluginsChanged -= ActiveAutoEnableOnce;
            }

#if DEBUG
            UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod.AutoEnableOnce({deferred}) plugins={mainMod != null}\n{(new System.Diagnostics.StackTrace(0, true)).ToString()}");
#endif

            if (mainMod != null)
            {
                bool enabled = false;
                if (!Versioning.IsObsolete(Versioning.Obsolescence.AUTO_MOD_ENABLE, "Auto-Mod Enabling for compatibility with old CitiesHarmony.API"))
                {

                    /* FIXME: When auto-subscribing, the API should also enable the mod.
                        * Older API versions do not enable it, so Enabling self here, for compatibility with old APIs.
                        * After a few updates with this warning, this behaviour should be removed.
                        */

                    {
#if DEBUG
                        UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] Found - {mainMod.name} - enabled={mainMod.isEnabled} - {mainMod.assembliesString}");
#endif
                        if (mainMod.ContainsAssembly(Assembly.GetExecutingAssembly()))
                        {
                            var oneShotAutoEnable = new SavedBool(name: mainMod.name + mainMod.modPath.GetHashCode().ToString() + ".enabled",
                                fileName: Settings.userGameState,
                                def: false,
                                autoUpdate: true);

                            if (!oneShotAutoEnable && !oneShotAutoEnable.exists)
                            {
                                UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO - Mod is disabled in the Content Manager! Self-enabling now.");

                                enabled = true;
                                firstRun = true;
                                mainMod.isEnabled = true;

                                /* FIXME: isEnabled vs oneShotAutoEnable.Value = true */
                            }
                            else
                            {
                                UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] WARNING - Mod is disabled in the Content Manager! Self-enabling has already been used.");
                            }
                        }
                    }
                }
            }
        }

#if DEVELOPER
        static void AutoInstallHelperOnce(PluginInfo plugin)
        {

#if DEBUG
            UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] In Mod.AutoInstallHelperOnce() plugin={plugin != null}");
#endif


            try
            {
                string helperPath = ColossalFramework.IO.DataLocation.modsPath + "\\" + RECOMMENDED_LOCAL_HELPER_DIRNAME;

                if (!Directory.Exists(helperPath))
                {
                    /* Enable it every time the directory needs to be recreated
                     * To avoid this, simply disable the local Diagnostics module
                     * when you don't want it, without deleting the folder
                     */
                    var localDiagnosticsModEnable = new SavedBool(name: RECOMMENDED_LOCAL_HELPER_DIRNAME + helperPath.GetHashCode().ToString() + ".enabled",
                        fileName: Settings.userGameState,
                        def: false,
                        autoUpdate: true).value = true;

                    UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] INFO - Installing local HELPER");

                    FileSystemUtils.CopyDirectoryFiltered(
                        plugin.modPath,
                        helperPath,
                        CopyAllButMyAssembly,
                        false);
                }
                HelperUpdate(plugin);
            }
            catch (Exception ex) {
                SelfReport(SelfProblemType.HelperInstallationFailed, ex);
            }
        }

        static bool HelperUpdate(PluginInfo self)
        {
            try
            {
                var helperFilename = Directory.GetFiles(self.modPath, Assembly.GetExecutingAssembly().GetName().Name + "_helper_dll");
                string helperDestination = ColossalFramework.IO.DataLocation.modsPath + "/" +
                    RECOMMENDED_LOCAL_HELPER_DIRNAME + "/" +
                    Assembly.GetExecutingAssembly().GetName().Name + ".dll";

                if (helperFilename.Length == 1)
                {
                    File.Copy(FileSystemUtils.NiceWinPath(helperFilename[0]), FileSystemUtils.NiceWinPath(helperDestination), true);
                    return true;
                }
                else
                {
                    throw new Exception("helper_dll file was not found");
                }
            }
            catch (Exception ex)
            {
                SelfReport(SelfProblemType.HelperInstallationFailed, new Exception("Updating Helper failed", ex));
            }

            return false;
        }

        static bool CopyAllButMyAssembly(string filename) {
            bool result = !filename.EndsWith(Assembly.GetExecutingAssembly().GetName().Name + ".dll") ||
                !filename.EndsWith(Assembly.GetExecutingAssembly().GetName().Name + "_helper_dll");
            UnityEngine.Debug.LogWarning($"[{Versioning.FULL_PACKAGE_NAME}] INFO - CopyAllButMyAssembly({filename}) = {result}");
            return result;
        }
#endif
#endregion

        internal static void SelfReport(SelfProblemType problem, Exception e = null)
        {
#if DEVELOPER
            /* skip reporting first run problems if the mod has just been installed */
            if (firstRun && problem == SelfProblemType.NotLoadedFirst)
                return;
#endif

            if (mainMod == null)
            {
                /* used when installing the helper for the first time,
                 * before handover
                 */
                UnityEngine.Debug.LogError($"[{Versioning.FULL_PACKAGE_NAME}] ERROR - (no mainMod) {Report.SelfProblemText(problem)}: {Report.ExMessage(e, true, 1)}");
            } else
            {
                /* this should also include an indication of who is reporting (eg, helper) */
                (mainMod.userModInstance as IAmAware).SelfReport(problem, e);
            }
        }

    }
}
