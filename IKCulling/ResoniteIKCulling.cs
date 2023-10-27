﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.FinalIK;
using HarmonyLib;
using ResoniteModLoader;

namespace IkCulling
{
    //Populated to every IK in dictionary.
    //We probably didn't need to do this, but in the event
    //that more variables per IK are needed, this is here.
    public class Variables
    {
        public int UpdateIndex = 1;
    }

    public class IkCulling : ResoniteMod
    {
        public static ModConfiguration Config;

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<bool> Enabled =
            new ModConfigurationKey<bool>(
                "Enabled",
                "ResoniteIkCulling is Enabled.",
                () => true);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<bool> DisableAfkUser =
            new ModConfigurationKey<bool>(
                "DisableAfkUser",
                "Disable IK of users not in the session.",
                () => true);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<bool> DisableIkWithoutUser =
            new ModConfigurationKey<bool>(
                "DisableIkWithoutUser",
                "Disable IK without an active user.",
                () => true);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<bool> UseUserScale =
            new ModConfigurationKey<bool>(
                "UseUserScale",
                "Use your scale for distance checks.",
                () => false);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<bool> UseOtherUserScale =
            new ModConfigurationKey<bool>(
                "UseOtherUserScale",
                "Use other user's scale for distance checks.",
                () => false);
                
        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<bool> IkUpdateFalloff =
            new ModConfigurationKey<bool>(
                "IkUpdateFalloff",
                "Reduce IK updates as they approach maximum range.",
                () => false);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<IkUpdateRate> UpdateRate =
            new ModConfigurationKey<IkUpdateRate>(
                "UpdateRate",
                "Update rate for IK.",
                () => IkUpdateRate.Full);

        [Range(0f, 360f)]
        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<float> FOV = 
            new ModConfigurationKey<float>(
                "FOV",
                "Field of view used for culling, ranging from 0 degrees to 360 degrees.",
                () => 110f, false, v => v <= 360f && v >= 0f);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<int> MinUserCount =
            new ModConfigurationKey<int>(
                "MinUserCount",
                "Minimum amount of active users in the world to enable culling.",
                () => 3);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<float> MinCullingRange =
            new ModConfigurationKey<float>(
                "MinCullingRange",
                "Minimum range for IK to always be enabled, useful for mirrors.",
                () => 4f);

        [AutoRegisterConfigKey] public static readonly ModConfigurationKey<float> MaxViewRange =
            new ModConfigurationKey<float>(
                "MaxViewRange",
                "Maximum range before fully disabling IK.",
                () => 30f);

        public override string Name => "ResoniteIkCulling";
        public override string Author => "Raidriar796 & KyuubiYoru";
        public override string Version => "2.3.3";
        public override string Link => "https://github.com/Raidriar796/ResoniteIkCulling";

        public override void OnEngineInit()
        {
            try
            {
                Harmony harmony = new Harmony("net.Raidriar796.ResoniteIkCulling");
                harmony.PatchAll();

                Config = GetConfiguration();

                Config.Save(true);
            }
            catch (Exception e)
            {
                Error(e.Message);
                Error(e.ToString());
                throw;
            }

            //Calling the methods on start instead of declaring the
            //variables with method calls because rml doesn't like that
            UpdateFOV();
            UpdateMinRange();
            UpdateMaxRange();

            //Sets up variables to avoid needless calculations per frame
            FOV.OnChanged += (value) => { UpdateFOV(); };
            MinCullingRange.OnChanged += (value) => { UpdateMinRange(); };
            MaxViewRange.OnChanged += (value) => { UpdateMaxRange(); };
        }
        
        public static float FOVDegToDot = 0;

        public static float MinCullingRangeSqr = 0;

        public static float MaxViewRangeSqr = 0;

        public static void UpdateFOV()
        {
            FOVDegToDot = MathX.Cos(0.01745329f * (Config.GetValue(FOV) * 0.5f));
        }

        public static void UpdateMinRange()
        {   
            MinCullingRangeSqr = MathX.Pow(Config.GetValue(MinCullingRange), 2f);
        }

        public static void UpdateMaxRange()
        {   
            MaxViewRangeSqr = MathX.Pow(Config.GetValue(MaxViewRange), 2f);
        }

        public enum IkUpdateRate
        {
            Full,
            Half,
            Quarter,
            Eighth
        }

        static Dictionary<VRIKAvatar, Variables> vrikList = new Dictionary<VRIKAvatar, Variables>();

        [HarmonyPatch("OnAwake")]
        [HarmonyPostfix]
        private static void AddToList(VRIKAvatar __instance)
        {
            //Adds IK to list as new instances appear
            if (!vrikList.ContainsKey(__instance)) {
                vrikList.Add(__instance, new Variables());
            }

            //Searches and removes all null IK in dictionary whenever an IK is added
            foreach (var item in vrikList)
            {
                if (item.Key == null) vrikList.Remove(item.Key);
            }
        }
        
        [HarmonyPatch(typeof(VRIKAvatar))]
        public class IkCullingPatch
        {
            [HarmonyPrefix]
            [HarmonyPatch("OnCommonUpdate")]
            private static bool OnCommonUpdatePrefix(VRIKAvatar __instance)
            {
                try
                {
                    //IkCulling is disabled
                    if (!Config.GetValue(Enabled)) return true;
                    
                    //Ik is disabled
                    if (!__instance.Enabled) return false;

                    //User is Headless
                    if (__instance.LocalUser.HeadDevice == HeadOutputDevice.Headless) return false;

                    //Always update local Ik
                    if (__instance.IsUnderLocalUser && __instance.IsEquipped) return true;
                    
                    //Too few users
                    if (__instance.Slot.World.ActiveUserCount < Config.GetValue(MinUserCount)) return true;

                    //Users not present
                    if (__instance.Slot.ActiveUser != null && Config.GetValue(DisableAfkUser) &&
                        !__instance.Slot.ActiveUser.IsPresentInWorld) return false;

                    //No active user
                    if (Config.GetValue(DisableIkWithoutUser) && !__instance.IsEquipped) return false;

                    
                    float3 playerPos = __instance.Slot.World.LocalUserViewPosition;
                    float3 ikPos = __instance.HeadProxy.GlobalPosition;

                    float dist = MathX.DistanceSqr(playerPos, ikPos);

                    //Include user scale in calculation
                    if (Config.GetValue(UseUserScale)) 
                        dist /= MathX.Pow(__instance.LocalUserRoot.GlobalScale, 2f);

                    //Include other user's scale in calculation
                    if (Config.GetValue(UseOtherUserScale) && __instance.Slot.ActiveUser != null)
                        dist /= MathX.Pow(__instance.Slot.ActiveUser.Root.GlobalScale, 2f);

                    //Check if IK is outside of max range
                    if (dist > MaxViewRangeSqr) 
                    return false;

                    //Checks if IK is within min range and in view
                    if (dist > MinCullingRangeSqr &&
                    MathX.Dot((ikPos - playerPos).Normalized, __instance.Slot.World.LocalUserViewRotation * float3.Forward) < FOVDegToDot) 
                    return false;

                    //IK throttling
                    if (Config.GetValue(IkUpdateFalloff) || (Config.GetValue(UpdateRate) != IkUpdateRate.Full) && __instance.Slot.ActiveUser != __instance.LocalUser) {

                        //Adds an IK instance to the list if it's not already
                        if (!vrikList.ContainsKey(__instance))
                        {
                            vrikList.Add(__instance, new Variables());
                            return true;
                        }

                        int skipCount = 1;
                        Variables current = vrikList[__instance];

                        //Update skips for falloff
                        if (Config.GetValue(IkUpdateFalloff)) 
                        {
                            if (dist > MaxViewRangeSqr * 0.81f)
                            {
                                skipCount = 6;
                            }
                            else if (dist > MaxViewRangeSqr * 0.64f)
                            {
                                skipCount = 5;
                            }
                            else if (dist > MaxViewRangeSqr * 0.49f)
                            {
                                skipCount = 4;
                            }
                            else if (dist > MaxViewRangeSqr * 0.36f)
                            {
                                skipCount = 3;
                            }
                            else if (dist > MaxViewRangeSqr * 0.25f)
                            {
                                skipCount = 2;
                            }
                        }

                        //Update skips lower update rate
                        else switch (Config.GetValue(UpdateRate))
                        {
                            case IkUpdateRate.Half:
                            skipCount = 2;
                            break;
                            
                            case IkUpdateRate.Quarter:
                            skipCount = 4;
                            break;

                            case IkUpdateRate.Eighth:
                            skipCount = 8;
                            break;

                            default:
                            skipCount = 1;
                            break;
                        }

                        //Update skips for falloff + lower update rate
                        if (Config.GetValue(UpdateRate) != IkUpdateRate.Full && Config.GetValue(IkUpdateFalloff))
                        {
                            switch (Config.GetValue(UpdateRate))
                            {
                                case IkUpdateRate.Half:
                                skipCount *= 2;
                                break;
                                
                                case IkUpdateRate.Quarter:
                                skipCount *= 4;
                                break;

                                case IkUpdateRate.Eighth:
                                skipCount *= 8;
                                break;

                                default:
                                skipCount = 1;
                                break;
                            }
                        }
                        

                        //The part that actually skips updates
                        if (vrikList[__instance].UpdateIndex > skipCount) vrikList[__instance].UpdateIndex = 1;
                        if (vrikList[__instance].UpdateIndex == skipCount)
                        {
                            vrikList[__instance].UpdateIndex = 1;
                            return true;
                        }
                        else
                        {
                            vrikList[__instance].UpdateIndex += 1;
                            return false;
                        }
                    }
                    return true;
                }
                catch (Exception e)
                {
                    Debug("Error in OnCommonUpdatePrefix");
                    Debug(e.Message);
                    Debug(e.StackTrace);
                    return true;
                }
            }
        }

        //Used to search through the entire FBT calibrator to enable "AutoUpdate" on every IK
        public static void CalibratorForceIkAutoUpdate(FullBodyCalibrator __instance) {
            var allVRIK = __instance.Slot.GetComponentsInChildren<VRIK>();
            foreach (var vrik in allVRIK) {
                vrik.AutoUpdate.Value = true;
            }
        }

        //Forces IK to be active when FBT calibrator is created
        [HarmonyPatch(typeof(FullBodyCalibrator), "OnAwake")]
        class FullBodyCalibratorPatch {
            
            [HarmonyPostfix]
            static void Postfix(FullBodyCalibrator __instance) {
                __instance.RunInUpdates(3, ()=> {
                    CalibratorForceIkAutoUpdate(__instance);
                });
            }
        }

        //Forces IK to be active when pressing "Calibrate Avatar" on the FBT calibrator
        [HarmonyPatch(typeof(FullBodyCalibrator), "CalibrateAvatar")]
        class FullBodyCalibratorAvatarPatch {

            [HarmonyPostfix]
            static void Postfix(FullBodyCalibrator __instance) {
                CalibratorForceIkAutoUpdate(__instance);
            }
            
        }    
    }
}