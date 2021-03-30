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
/*
MIT License

Copyright (c) 2017 Felix Schmidt

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

/* **************************************************************************
 * 
 * 
 * IMPORTANT IMPORTANT IMPORTANT IMPORTANT IMPORTANT IMPORTANT IMPORTANT IMPORTANT
 * 
 * This file contains leftover code from the initial fork from Felix Schmidt's
 * repository https://github.com/boformer/CitiesHarmony
 * 
 * It contains known bad code, which is either not used at all in my implementation,
 * or it is in the course of being re-written. If I am rewriting it, I only included
 * it because an initial release of my project was needed urgently to address
 * a broken modding eco-system in Cities Skylines, and I considered it will do no
 * further harm over what has already been done by Felix Schmidt's code.
 * 
 * I would recommend you do not copy or rely on this code. A near future update will
 * remove this and replace it with proper code I would be proud to put my name on.
 * 
 * Until then, the copyright notice above was expressely requested by Felix Schmidt,
 * by means of a DMCA complaint at GitHub and Steam.
 * 
 * There is no code between the end of this comment and he "END-OF-Felix Schmidt-COPYRIGHT"
 * line if there is one, or the end of the file, that I, Radu Hociung, claim any copyright
 * on. The rest of the content, outside of these delimiters, is my copyright, and
 * you may copy it in accordance to the modified GPL license at the root or the repo
 * (LICENSE file)
 * 
 * FUTHER, if you base your code on a copy of the example mod from Felix Schmidt's
 * repository, which does not include his copyright notice, you will likely also
 * be a victim of DMCA complaint from him.
 */
extern alias Harmony2;
using Harmony2::HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HarmonyMod {
    /// <summary>
    /// 1. Reverts Harmony 1.x patches that were applied before this mod was loaded.<br/>
    /// 2. Resets the Harmony shared state so that Harmony 2.x runs without exceptions.<br/>
    /// 3. Self-patches the Harmony 1.2 assembly so that it redirects all calls to Harmony 2.x.<br/>
    /// 4. Re-applies the patches using Harmony 2.x
    /// </summary>
    class Harmony1StateTransfer {
        private MethodInfo HarmonySharedState_GetPatchedMethods;
        private MethodInfo HarmonySharedState_GetPatchInfo;

        private FieldInfo PatchInfo_prefixed;
        private FieldInfo PatchInfo_postfixes;
        private FieldInfo PatchInfo_transpilers;

        private FieldInfo Patch_owner;
        private FieldInfo Patch_priority;
        private FieldInfo Patch_before;
        private FieldInfo Patch_after;
        private FieldInfo Patch_patch;

        private Type harmonyInstanceType;
        private MethodInfo HarmonyInstance_Create;
        private MethodInfo HarmonyInstance_Unpatch;

        private object HarmonyPatchType_All;

        public Harmony1StateTransfer(Assembly assembly) {

            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] Transferring Harmony {assembly.GetName().Version} state ({assembly.FullName})");

            var sharedStateType = assembly.GetType("Harmony.HarmonySharedState");
            HarmonySharedState_GetPatchedMethods = sharedStateType.GetMethodOrThrow("GetPatchedMethods", BindingFlags.NonPublic | BindingFlags.Static);
            HarmonySharedState_GetPatchInfo = sharedStateType.GetMethodOrThrow("GetPatchInfo", BindingFlags.NonPublic | BindingFlags.Static);

            var patchInfoType = assembly.GetType("Harmony.PatchInfo");
            PatchInfo_prefixed = patchInfoType.GetFieldOrThrow("prefixes");
            PatchInfo_postfixes = patchInfoType.GetFieldOrThrow("postfixes");
            PatchInfo_transpilers = patchInfoType.GetFieldOrThrow("transpilers");

            var patchType = assembly.GetType("Harmony.Patch");
            Patch_owner = patchType.GetFieldOrThrow("owner");
            Patch_priority = patchType.GetFieldOrThrow("priority");
            Patch_before = patchType.GetFieldOrThrow("before");
            Patch_after = patchType.GetFieldOrThrow("after");
            Patch_patch = patchType.GetFieldOrThrow("patch");

            harmonyInstanceType = assembly.GetType("Harmony.HarmonyInstance") ?? throw new Exception("HarmonyInstance type not found");
            HarmonyInstance_Create = harmonyInstanceType.GetMethodOrThrow("Create", BindingFlags.Public | BindingFlags.Static);

            var harmonyPatchTypeType = assembly.GetType("Harmony.HarmonyPatchType") ?? throw new Exception("HarmonyPatchType type not found");

            var unpatchArgTypes = new Type[] { typeof(MethodBase), harmonyPatchTypeType, typeof(string) };
            HarmonyInstance_Unpatch = HarmonyInstance_Unpatch = harmonyInstanceType.GetMethod("RemovePatch", unpatchArgTypes) // Harmony 1.1.0.0
                ?? harmonyInstanceType.GetMethodOrThrow("Unpatch", unpatchArgTypes); // Harmony 1.2.0.1

            HarmonyPatchType_All = Enum.ToObject(harmonyPatchTypeType, 0);
        }

        public void Patch() {
            var patchedMethods = new List<MethodBase>((HarmonySharedState_GetPatchedMethods.Invoke(null, new object[0]) as IEnumerable<MethodBase>));

            UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] {patchedMethods.Count} patched methods found.");

            if (patchedMethods.Count != 0)
            {
                var processors = new List<PatchProcessor>();

                foreach (var method in patchedMethods) {
                    var patchInfo = HarmonySharedState_GetPatchInfo.Invoke(null, new object[] { method });
                    if (patchInfo == null) continue;

                    var prefixes = (object[])PatchInfo_prefixed.GetValue(patchInfo);
                    foreach (var patch in prefixes) {
                        processors.Add(CreateHarmony(patch)
                            .CreateProcessor(method)
                            .AddPrefix(CreateHarmonyMethod(patch)));
                    }

                    var postfixes = (object[])PatchInfo_postfixes.GetValue(patchInfo);
                    foreach (var patch in postfixes) {
                        processors.Add(CreateHarmony(patch)
                            .CreateProcessor(method)
                            .AddPostfix(CreateHarmonyMethod(patch)));
                    }

                    var transpilers = (object[])PatchInfo_transpilers.GetValue(patchInfo);
                    foreach (var patch in transpilers) {
                        processors.Add(CreateHarmony(patch)
                            .CreateProcessor(method)
                            .AddTranspiler(CreateHarmonyMethod(patch)));
                    }
                }

                UnityEngine.Debug.Log($"[{Versioning.FULL_PACKAGE_NAME}] Reverting patches...");
                var oldInstance = HarmonyInstance_Create.Invoke(null, new object[] { "HarmonyMod" });
                foreach (var method in patchedMethods.ToList())
                {
                    HarmonyInstance_Unpatch.Invoke(oldInstance, new object[] { method, HarmonyPatchType_All, null });
                }

                // Reset shared state
                var sharedStateAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name.Contains("HarmonySharedState"));
                if (sharedStateAssembly != null) {
                    var stateField = sharedStateAssembly.GetType("HarmonySharedState")?.GetField("state");
                    if (stateField != null) {
                        UnityEngine.Debug.Log("Resetting HarmonySharedState...");
                        stateField.SetValue(null, null);
                    }
                }

                // Apply patches using Harmony 2.x
                foreach (var processor in processors) {
                    processor.Patch();
                }
            }
        }

        private Harmony CreateHarmony(object patch) {
            var owner = (string)Patch_owner.GetValue(patch);
            return new Harmony(owner);
        }

        private HarmonyMethod CreateHarmonyMethod(object patch) {
            var priority = (int)Patch_priority.GetValue(patch);
            var before = (string[])Patch_before.GetValue(patch);
            var after = (string[])Patch_after.GetValue(patch);
            var method = (MethodInfo)Patch_patch.GetValue(patch);
            return new HarmonyMethod(method, priority, before, after);
        }
    }
}