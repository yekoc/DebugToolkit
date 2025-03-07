using DebugToolkit.Commands;
using DebugToolkit.Permissions;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using R2API.Utils;
using RoR2;
using RoR2.ConVar;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using Console = RoR2.Console;

namespace DebugToolkit
{
    public sealed class Hooks
    {
        private const ConVarFlags AllFlagsNoCheat = ConVarFlags.None | ConVarFlags.Archive | ConVarFlags.Engine | ConVarFlags.ExecuteOnServer | ConVarFlags.SenderMustBeServer;

        private static On.RoR2.Console.orig_RunCmd _origRunCmd;
        private static CharacterMaster pingedTarget;
        public static void InitializeHooks()
        {
            IL.RoR2.Console.Awake += UnlockConsole;
            On.RoR2.Console.InitConVars += InitCommandsAndFreeConvars;

            var runCmdHook = new Hook(typeof(Console).GetMethodCached("RunCmd"),
                typeof(Hooks).GetMethodCached(nameof(LogNetworkCommandsAndCheckPermissions)), new HookConfig { Priority = 1 });
            _origRunCmd = runCmdHook.GenerateTrampoline<On.RoR2.Console.orig_RunCmd>();

            On.RoR2.Console.AutoComplete.SetSearchString += BetterAutoCompletion;
            On.RoR2.Console.AutoComplete.ctor += CommandArgsAutoCompletion;
            RoR2Application.onLoad += ArgsAutoCompletion.GatherCommandsAndFillStaticArgs;
            IL.RoR2.UI.ConsoleWindow.Update += SmoothDropDownSuggestionNavigation;
            IL.RoR2.Networking.NetworkManagerSystem.CCSetScene += EnableCheatsInCCSetScene;
            On.RoR2.Networking.NetworkManagerSystem.CCSceneList += OverrideVanillaSceneList;
            On.RoR2.PingerController.RebuildPing += InterceptPing;

            // Noclip hooks
            var hookConfig = new HookConfig { ManualApply = true };
            Command_Noclip.OnServerChangeSceneHook = new Hook(typeof(UnityEngine.Networking.NetworkManager).GetMethodCached("ServerChangeScene"),
    typeof(Command_Noclip).GetMethodCached("DisableOnServerSceneChange"), hookConfig);
            Command_Noclip.origServerChangeScene = Command_Noclip.OnServerChangeSceneHook.GenerateTrampoline<Command_Noclip.d_ServerChangeScene>();
            Command_Noclip.OnClientChangeSceneHook = new Hook(typeof(UnityEngine.Networking.NetworkManager).GetMethodCached("ClientChangeScene"),
                typeof(Command_Noclip).GetMethodCached("DisableOnClientSceneChange"), hookConfig);
            Command_Noclip.origClientChangeScene = Command_Noclip.OnClientChangeSceneHook.GenerateTrampoline<Command_Noclip.d_ClientChangeScene>();
        }

        private static void InterceptPing(On.RoR2.PingerController.orig_RebuildPing orig, PingerController self, PingerController.PingInfo pingInfo)
        {
            orig(self, pingInfo);
            if (self.pingIndicator && self.pingIndicator.pingTarget)
            {
                pingedTarget = self.pingIndicator.pingTarget.GetComponent<CharacterBody>()?.master;
                return;
            }
            pingedTarget = null;
        }

        private static void OverrideVanillaSceneList(On.RoR2.Networking.NetworkManagerSystem.orig_CCSceneList orig, ConCommandArgs args)
        {
            StringBuilder stringBuilder = new StringBuilder();
            foreach (var sceneDef in SceneCatalog.allSceneDefs.Where((def) => !Run.instance || !def.requiredExpansion || Run.instance.IsExpansionEnabled(def.requiredExpansion)))
            {
                stringBuilder.AppendLine($"[{sceneDef.sceneDefIndex}] - {sceneDef.cachedName} ");
            }
            Log.Message(stringBuilder, Log.LogLevel.MessageClientOnly);
        }

        private static void EnableCheatsInCCSetScene(ILContext il)
        {
            ILCursor c = new ILCursor(il);
            var method = typeof(SceneCatalog).GetMethodCached(nameof(SceneCatalog.GetSceneDefFromSceneName));
            var newMethod = typeof(Hooks).GetMethodCached(nameof(BetterSceneDefFinder));
            c.GotoNext(MoveType.Before,
                x => x.MatchCallOrCallvirt(method)
                );
            c.Remove();
            c.Emit(OpCodes.Call, newMethod);

            c.GotoNext(MoveType.After,
                x => x.MatchCallOrCallvirt<RoR2.Console.CheatsConVar>("get_boolValue")
                );
            c.Emit(OpCodes.Pop);
            c.Emit(OpCodes.Ldc_I4_1);
        }

        public static SceneDef BetterSceneDefFinder(string sceneName)
        {
            int index = -1;
            if (int.TryParse(sceneName, out index))
            {
                if (index > -1 && index < SceneCatalog.allSceneDefs.Length)
                {
                    var def = SceneCatalog.allSceneDefs[index];
                    if (!Run.instance || !def.requiredExpansion || Run.instance.IsExpansionEnabled(def.requiredExpansion))
                    {
                        return def;
                    }
                }
            }

            var scenes = SceneCatalog.allSceneDefs.Where((def) => def.cachedName == sceneName);
            if (!scenes.Any())
            {
                return null;
            }
            if (Run.instance)
            {
                //Sorry :/
                scenes = scenes.Where((def) => !def.requiredExpansion || Run.instance.IsExpansionEnabled(def.requiredExpansion));
            }

            var matchedNetworkScenes = scenes.Where((def) => RoR2.Networking.NetworkManagerSystem.singleton && UnityEngine.Networking.NetworkManager.singleton.isNetworkActive != def.isOfflineScene);
            if (matchedNetworkScenes.Any())
            {
                return matchedNetworkScenes.First();
            }

            return scenes.First();
        }

        private static void UnlockConsole(ILContext il)
        {
            ILCursor c = new ILCursor(il);
            c.GotoNext(
                MoveType.After,
                x => x.MatchCastclass(typeof(ConCommandAttribute))
                );
            c.EmitDelegate<Func<ConCommandAttribute, ConCommandAttribute>>(
                (conAttr) =>
                {
                    conAttr.flags &= AllFlagsNoCheat;
                    if (conAttr.commandName == "run_set_stages_cleared")
                    {
                        conAttr.helpText = Lang.RUNSETSTAGESCLEARED_HELP;
                    }
                    return conAttr;
                });
        }

        private static void InitCommandsAndFreeConvars(On.RoR2.Console.orig_InitConVars orig, Console self)
        {
            void RemoveCheatFlag(BaseConVar cv)
            {
                cv.flags &= AllFlagsNoCheat;
            }

            orig(self);

            RemoveCheatFlag(self.FindConVar("sv_time_transmit_interval"));
            RemoveCheatFlag(self.FindConVar("run_scene_override"));
            RemoveCheatFlag(self.FindConVar("stage1_pod"));

            self.FindConVar("timescale").helpText += " Use time_scale instead!";
            self.FindConVar("director_combat_disable").helpText += " Use no_enemies instead!";
            self.FindConVar("timestep").helpText += " Let the DebugToolkit team know if you need this convar.";
            self.FindConVar("cmotor_safe_collision_step_threshold").helpText += " Let the DebugToolkit team know if you need this convar.";
            self.FindConVar("cheats").helpText += " But you already have the DebugToolkit mod installed...";
        }

        // ReSharper disable once UnusedMember.Local
        private static void LogNetworkCommandsAndCheckPermissions(Console self, Console.CmdSender sender, string concommandName, List<string> userArgs)
        {
            var sb = new StringBuilder();
            userArgs.ForEach((str) => sb.AppendLine(str));

            if (sender.networkUser != null && sender.localUser == null)
            {
                Log.Message(string.Format(Lang.NETWORKING_OTHERPLAYER_4, sender.networkUser.userName, sender.networkUser.id.value, concommandName, sb));
            }
            else if (Application.isBatchMode)
            {
                Log.Message(string.Format(Lang.NETWORKING_OTHERPLAYER_4, "Server", 0, concommandName, sb));
            }

            var canExecute = true;

            if (sender.networkUser != null && sender.localUser == null && PermissionSystem.IsEnabled.Value)
            {
                canExecute = PermissionSystem.CanUserExecute(sender.networkUser, concommandName, userArgs);
            }

            if (canExecute)
            {
                _origRunCmd(self, sender, concommandName, userArgs);
                ScrollConsoleDown();
            }
        }

        internal static void ScrollConsoleDown()
        {
            if (RoR2.UI.ConsoleWindow.instance && RoR2.UI.ConsoleWindow.instance.outputField.verticalScrollbar)
            {
                RoR2.UI.ConsoleWindow.instance.outputField.verticalScrollbar.value = 1f;
            }
        }

        private static bool BetterAutoCompletion(On.RoR2.Console.AutoComplete.orig_SetSearchString orig, Console.AutoComplete self, string newSearchString)
        {
            var searchString = self.GetFieldValue<string>("searchString");
            var searchableStrings = self.GetFieldValue<List<string>>("searchableStrings");

            newSearchString = newSearchString.ToLower(CultureInfo.InvariantCulture);
            if (newSearchString == searchString)
            {
                return false;
            }
            self.SetFieldValue("searchString", newSearchString);

            self.resultsList = new List<string>();
            foreach (var searchableString in searchableStrings)
            {
                if (searchableString.ToLower(CultureInfo.InvariantCulture).Contains(newSearchString)) // StartWith case
                {
                    self.resultsList.Add(searchableString);
                }
                else // similar string in the middle of the user command arg
                {
                    string searchableStringsInvariant = searchableString.ToLower(CultureInfo.InvariantCulture);
                    string userArg = newSearchString.Substring(newSearchString.IndexOf(' ') + 1);
                    if (newSearchString.IndexOf(' ') > 0 && searchableString.IndexOf(' ') > 0)
                    {
                        string userCmd = newSearchString.Substring(0, newSearchString.IndexOf(' '));
                        string searchableStringsCmd = searchableString.Substring(0, searchableString.IndexOf(' '));
                        string searchableStringsArg = searchableStringsInvariant.Substring(searchableStringsInvariant.IndexOf(' ') + 1);
                        if (searchableStringsArg.Contains(userArg) && userCmd.Equals(searchableStringsCmd))
                        {
                            self.resultsList.Add(searchableString);
                        }
                    }
                }
            }
            return true;
        }

        private static void CommandArgsAutoCompletion(On.RoR2.Console.AutoComplete.orig_ctor orig, Console.AutoComplete self, Console console)
        {
            orig(self, console);

            var searchableStrings = self.GetFieldValue<List<string>>("searchableStrings");
            var tmp = new List<string>();

            tmp.AddRange(ArgsAutoCompletion.CommandsWithStaticArgs);
            tmp.AddRange(ArgsAutoCompletion.CommandsWithDynamicArgs());

            tmp.Sort();
            searchableStrings.AddRange(tmp);

            self.SetFieldValue("searchableStrings", searchableStrings);
        }

        private const float changeSelectedItemTimer = 0.1f;
        private static float _lastSelectedItemChange;
        private static void SmoothDropDownSuggestionNavigation(ILContext il)
        {
            var cursor = new ILCursor(il);

            var getKey = il.Import(typeof(Input).GetMethodCached("GetKey", new[] { typeof(KeyCode) }));

            cursor.GotoNext(
                MoveType.After,
                x => x.MatchLdcI4(0x111),
                x => x.MatchCallOrCallvirt<Input>("GetKeyDown")
            );
            cursor.Prev.Operand = getKey;
            cursor.EmitDelegate<Func<bool, bool>>(LimitChangeItemFrequency);

            cursor.GotoNext(
                MoveType.After,
                x => x.MatchLdcI4(0x112),
                x => x.MatchCallOrCallvirt<Input>("GetKeyDown")
            );
            cursor.Prev.Operand = getKey;
            cursor.EmitDelegate<Func<bool, bool>>(LimitChangeItemFrequency);

            bool LimitChangeItemFrequency(bool canChangeItem)
            {
                if (canChangeItem)
                {
                    var timeNow = Time.time;
                    if (timeNow > changeSelectedItemTimer + _lastSelectedItemChange)
                    {
                        _lastSelectedItemChange = timeNow;

                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }

                return canChangeItem;
            }
        }

        internal static WeightedSelection<DirectorCardCategorySelection> ForceFamilyEventForDccsPoolStages(On.RoR2.DccsPool.orig_GenerateWeightedSelection orig, DccsPool self)
        {
            if (ClassicStageInfo.instance.monsterDccsPool != self)
            {
                return orig(self);
            }

            On.RoR2.FamilyDirectorCardCategorySelection.IsAvailable += ForceFamilyDirectorCardCategorySelectionToBeAvailable;

            var weightedSelection = orig(self);

            On.RoR2.FamilyDirectorCardCategorySelection.IsAvailable -= ForceFamilyDirectorCardCategorySelectionToBeAvailable;

            var newChoices = new List<WeightedSelection<DirectorCardCategorySelection>.ChoiceInfo>();
            foreach (var choice in weightedSelection.choices)
            {
                if (choice.value && choice.value is FamilyDirectorCardCategorySelection)
                {
                    newChoices.Add(choice);
                }
            }

            weightedSelection.choices = newChoices.ToArray();
            weightedSelection.Count = newChoices.Count;
            weightedSelection.RecalculateTotalWeight();

            On.RoR2.DccsPool.GenerateWeightedSelection -= ForceFamilyEventForDccsPoolStages;

            return weightedSelection;
        }

        private static bool ForceFamilyDirectorCardCategorySelectionToBeAvailable(On.RoR2.FamilyDirectorCardCategorySelection.orig_IsAvailable orig, FamilyDirectorCardCategorySelection self)
        {
            return true;
        }

        internal static void ForceFamilyEventForNonDccsPoolStages(On.RoR2.ClassicStageInfo.orig_RebuildCards orig, ClassicStageInfo self)
        {
            var originalValue = ClassicStageInfo.monsterFamilyChance;

            ClassicStageInfo.monsterFamilyChance = 1;

            orig(self);

            ClassicStageInfo.monsterFamilyChance = originalValue;

            On.RoR2.ClassicStageInfo.RebuildCards -= ForceFamilyEventForNonDccsPoolStages;
        }

        internal static void CombatDirector_SetNextSpawnAsBoss(On.RoR2.CombatDirector.orig_SetNextSpawnAsBoss orig, CombatDirector self)
        {
            orig(self);

            var selectedBossCard = CurrentRun.nextBoss;

            var eliteTierDef = Spawners.GetTierDef(CurrentRun.nextBossElite.eliteIndex);

            selectedBossCard.spawnCard.directorCreditCost = (int)((self.monsterCredit / CurrentRun.nextBossCount) / eliteTierDef.costMultiplier);

            self.OverrideCurrentMonsterCard(selectedBossCard);

            self.currentActiveEliteTier = eliteTierDef;
            self.currentActiveEliteDef = CurrentRun.nextBossElite;

            Log.Message($"{selectedBossCard.spawnCard.name} cost has been set to {selectedBossCard.cost} for {CurrentRun.nextBossCount} {CurrentRun.nextBossElite} bosses with available credit: {self.monsterCredit}", Log.LogLevel.Info);

            CurrentRun.nextBossCount = 1;
            CurrentRun.nextBossElite = CombatDirector.eliteTiers[0].GetRandomAvailableEliteDef(self.rng);

            On.RoR2.CombatDirector.SetNextSpawnAsBoss -= CombatDirector_SetNextSpawnAsBoss;
        }

        internal static void SeedHook(On.RoR2.PreGameController.orig_Awake orig, PreGameController self)
        {
            orig(self);
            if (CurrentRun.seed != 0)
            {
                self.runSeed = CurrentRun.seed;
            }
        }

        internal static void OnPrePopulateSetMonsterCreditZero(SceneDirector director)
        {
            //Note that this is not a hook, but an event subscription.
            director.SetFieldValue("monsterCredit", 0);
        }

        internal static void DenyExperience(On.RoR2.ExperienceManager.orig_AwardExperience orig, ExperienceManager self, Vector3 origin, CharacterBody body, ulong amount)
        {
            return;
        }

        internal static CharacterMaster GetPingedTarget()
        {
            return pingedTarget;
        }
    }
}
