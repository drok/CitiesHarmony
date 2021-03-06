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


namespace HarmonyMod
{
    using IAwareness;
    using Harmony2::HarmonyLib;
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using UnityEngine.Assertions;

    public class Patcher
    {
        const string HARMONY_ID = "org.ohmi.harmony";

        bool initialized_ = false;

        readonly string id;
        readonly Harmony harmony;
        /* separate instance for patching harmony1; these patches will not be
         * removed when the HarmonyMod unloads, because it doesn't unload last yet
         * and other mods still need their 2.x access though 1.2.0.1 api
         */
        readonly string compat_id;
        readonly Harmony compatHarmony;
        readonly Harmony1SelfPatcher harmony1SelfPatcher;
        readonly bool foundUnsupportedHarmonyLib = false;

        IAmAware self;

        internal Patcher(IAmAware selfMod, string name, bool onAwarenessCallback = false)
        {
            id = HARMONY_ID + (name != null ? "+" + name : string.Empty);
            compat_id = id + "+Compat";
#if DEBUG
            Harmony.DEBUG = true;
#endif

            self = selfMod;
            harmony1SelfPatcher = new Harmony1SelfPatcher();

            EnableHarmony();
            harmony = new Harmony(id);
            compatHarmony = new Harmony(compat_id);


            if (!Harmony.Harmony1Patched)
            {
                Harmony.Harmony1Patched = true;
                try
                {
                    ImplementAdditionalVersionSupport(true);
                }
                catch (HarmonyModSupportException ex)
                {
                    foundUnsupportedHarmonyLib = true;
                    DisableHarmony();
                    (self as Mod).report.ReportUnsupportedHarmony(ex);
                }
            }
        }

        internal void PatchHarmony1(Assembly assembly)
        {
            harmony1SelfPatcher.Apply(compatHarmony, assembly);
        }
        bool EnableHarmony()
        {
            bool wasInitialized = Harmony.m_enabled.HasValue;
            Harmony.isEnabled = true;
            if (self != null)
            {
                Harmony.awarenessInstance = self;
                Harmony2010::HarmonyLib.Harmony.awarenessInstance = self;
                Harmony2009::HarmonyLib.Harmony.awarenessInstance = self;
                HarmonyCHH2040::HarmonyLib.Harmony.awarenessInstance = self;
            }
            Harmony2009::HarmonyLib.Harmony.isEnabled = true;
            Harmony2010::HarmonyLib.Harmony.isEnabled = true;
            HarmonyCHH2040::HarmonyLib.Harmony.isEnabled = true;
            if (!Harmony.harmonyUsers.ContainsKey(Assembly.GetExecutingAssembly().FullName)) {
                Harmony.harmonyUsers[Assembly.GetExecutingAssembly().FullName] = new Harmony.HarmonyUser() { checkBeforeUse = true, legacyCaller = false, instancesCreated = 0, };
            }

            return wasInitialized;
        }

        void DisableHarmony()
        {
            Harmony.isEnabled = false;
            Harmony2009::HarmonyLib.Harmony.isEnabled = false;
            Harmony2010::HarmonyLib.Harmony.isEnabled = false;
            HarmonyCHH2040::HarmonyLib.Harmony.isEnabled = false;
        }

        internal bool Install()
        {
            Assert.IsFalse(initialized_,
                "Should not call Patcher.Install() more than once");
#if TRACE
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: Installing patches as {id}");
#endif

            try
            {
                EnableHarmony();
            }
            catch (Exception ex)
            {
                /* This happens if another mod has a copy of 0Harmony.dll with version=2.0.4.0 (same Version as my own used), which gets loaded instead of mine. */
                self.SelfReport(SelfProblemType.WrongHarmonyLib, ex);
            }

            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                if (foundUnsupportedHarmonyLib)
                    DisableHarmony();

                initialized_ = true;
            }
            catch (Exception e)
            {
                Mod.SelfReport(SelfProblemType.OwnPatchInstallation, e);
            }

            return initialized_;
        }

        internal void Uninstall()
        {
            Assert.IsTrue(initialized_,
                "Should not call Patcher.Uninstall() more than once");

#if TRACE
            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] INFO: Removing patches as {id}");
#endif

            Assert.IsNotNull(harmony, "HarmonyInst != null");
            try
            {
                if (foundUnsupportedHarmonyLib)
                    EnableHarmony();

                harmony.UnpatchAll(id);

                /* FIXME: When harmonymod is removed last, it's
                 * safe to also remove Harmon1 patches.
                 *
                 * compatHarmony.UnpatchAll(compat_id);
                 */
                if (foundUnsupportedHarmonyLib)
                    DisableHarmony();
            }
            catch (Exception e)
            {
                Mod.SelfReport(SelfProblemType.OwnPatchRemoval, e);
            }

            initialized_ = false;
        }

        internal void UninstallAll()
        {
            Assert.IsTrue(Harmony.isEnabled,
                "Harmony should be enabled before UninstallAll");

            /* FIXME - implement disabling Harmony should remove all dependent mods' patches */
            // harmony.UnpatchAll();
        }

        internal void ImplementAdditionalVersionSupport(bool needHarmon1StateTransfer)
        {
            /* FIXME: Move this table to the API, so mods
             * can query for support at runtime.
             */
            Version[] harmonyVersionSupport = new Version[]
            {
                new Version(1, 0, 9, 1),
                new Version(1, 1, 0, 0),
                new Version(1, 2, 0, 1),
                new Version(2, 0, 0, 9),
                new Version(2, 0, 1, 0),
                new Version(2, 0, 4, 0),
            };

            /* Official support cut-off. Above this version, I will implement support.
             * Below this version, you need to implement the support and submit a
             * pull request; see below
             */
            Version minSupportedVersion = new Version(2, 0, 1, 0);

            List<HarmonyModSupportException.UnsupportedAssembly> unsupportedAssemblies = new List<HarmonyModSupportException.UnsupportedAssembly>();

            /* Enable the main Library to enable state transition, but
             * disable it again if transition failed or unsupported harmony libs exist
             * 
             */
            EnableHarmony();
            int failures = 0;

            /* FIXME: This should be done in order of decreasing Version */
            AppDomain.CurrentDomain.GetAssemblies()
                .DoIf((assembly) =>
                {
                    var your = assembly.GetName();
                    if (your.Name == "0Harmony")
                    {
                        if (Array.Exists(harmonyVersionSupport, (supported) => supported == your.Version))
                        {
                            return your.Version < new Version(2,0);
                        }
                        else if (your.Version < minSupportedVersion)
                        {
                            /* If you are a mod developer and came here due to the exception below,
                             * it is likely because you're trying to use a version older than 2.0.1.0
                             * which is not yet supported. You need to submit a pull-request to
                             * the HarmonyMod which implements the necesary State Transfer and
                             * runtime compatibility patch, or alternately a stub for your version.
                             */
                            unsupportedAssemblies.Add(new HarmonyModSupportException.UnsupportedAssembly(assembly, true));
                        }
                        else
                        {
                            /* If you are a mod developer and came here due to the exception below,
                             * you're trying to use a version of 0Harmony which should be supported,
                             * but is not currently.
                             * Enter a feature request at https://github.com/drok/Harmony-CitiesSkylines/issues
                             * For quicker service, include a pull request, implementing the appropriate
                             * stub branch (you can use the branch stub-v2.0.1.0-to-current as example)
                             * for your desired version
                             *
                             * Note if the version you're looking for is a new release from the HarmonyLib
                             * developer, the HarmonyMod author will wait for some time for any bugs to shake
                             * out of the new release before adding support to HarmonyMod. You can make this
                             * happen quicker by reviewing pardeike's new release, report any bugs, and help
                             * get them fixed. Mention that you've reviewed pardeike's release notes and
                             * commits with your pull request. It'll speed up its implementation.
                             *
                             * Note that your pull request must include test cases for the new features
                             * implemented in the new Harmony version, and the test cases should be particularly
                             * thorough about the features you're interested in using.
                             *
                             * The goal is to add new feature support without compromising stability of the
                             * existing ecosystem.
                             */
                            unsupportedAssemblies.Add(new HarmonyModSupportException.UnsupportedAssembly(assembly, false));
                        }
                    }
                    return false;
                }, (a) => {
                    try
                    {
                        if (needHarmon1StateTransfer)
                            new Harmony1StateTransfer(a).Patch();
                        PatchHarmony1(a);
                    }
                    catch (Exception e)
                    {
                        Mod.SelfReport(SelfProblemType.CompatibilityPatchesFailed, e);
                        failures++;
                    }
                });

            if (failures != 0 || unsupportedAssemblies.Count != 0)
            {
                DisableHarmony();
            }

            if (unsupportedAssemblies.Count != 0)
            {
                Harmony.unsupportedException = new HarmonyModSupportException(unsupportedAssemblies);
                throw Harmony.unsupportedException;
            }
        }
        internal static bool isHarmonyUserException(Exception e)
        {
            return e is Harmony2.HarmonyLib.HarmonyUserException ||
                e is Harmony2009::HarmonyLib.HarmonyUserException ||
                e is Harmony2010::HarmonyLib.HarmonyUserException ||
                e is HarmonyCHH2040::HarmonyLib.HarmonyUserException;
        }

    }
}
