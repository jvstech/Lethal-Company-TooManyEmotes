﻿using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TooManyEmotes.Audio;
using TooManyEmotes.Networking;
using TooManyEmotes.Patches;
using Unity.Netcode;
using static TooManyEmotes.HelperTools;
using static TooManyEmotes.CustomLogging;

namespace TooManyEmotes
{
    [HarmonyPatch]
    public static class SessionManager
    {
        public static string localPlayerUsername { get { return GameNetworkManager.Instance?.username; } }

        internal static List<UnlockableEmote> unlockedEmotes = new List<UnlockableEmote>();
        internal static List<UnlockableEmote> unlockedEmotesTier0 = new List<UnlockableEmote>();
        internal static List<UnlockableEmote> unlockedEmotesTier1 = new List<UnlockableEmote>();
        internal static List<UnlockableEmote> unlockedEmotesTier2 = new List<UnlockableEmote>();
        internal static List<UnlockableEmote> unlockedEmotesTier3 = new List<UnlockableEmote>();
        
        internal static Dictionary<string, List<UnlockableEmote>> unlockedEmotesByPlayer = new Dictionary<string, List<UnlockableEmote>>();
        internal static List<UnlockableEmote> unlockedFavoriteEmotes = new List<UnlockableEmote>();


        [HarmonyPatch(typeof(StartOfRound), "Awake")]
        [HarmonyPostfix]
        public static void ResetGameValues()
        {
            EmoteController.allEmoteControllers?.Clear();
            EmoteControllerPlayer.allPlayerEmoteControllers?.Clear();
            EmoteControllerMaskedEnemy.allMaskedEnemyEmoteControllers?.Clear();
            EmoteAudioSource.allEmoteAudioSources?.Clear();
        }


        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        public static void OnHostConnected(PlayerControllerB __instance)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            if (!unlockedEmotesByPlayer.ContainsKey(StartOfRound.Instance.localPlayerController.playerUsername))
            {
                unlockedEmotesByPlayer.Add(StartOfRound.Instance.localPlayerController.playerUsername, unlockedEmotes);
                TerminalPatcher.currentEmoteCreditsByPlayer.Add(StartOfRound.Instance.localPlayerController.playerUsername, TerminalPatcher.currentEmoteCredits);
            }
            else
            {
                foreach (var emote in unlockedEmotesByPlayer[StartOfRound.Instance.localPlayerController.playerUsername])
                {
                    if (!IsEmoteUnlocked(emote))
                        unlockedEmotes.Add(emote);
                }
                unlockedEmotesByPlayer[StartOfRound.Instance.localPlayerController.playerUsername] = unlockedEmotes;
                TerminalPatcher.currentEmoteCreditsByPlayer[StartOfRound.Instance.localPlayerController.playerUsername] = TerminalPatcher.currentEmoteCredits;
            }
        }


        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        public static void OnServerStart(StartOfRound __instance)
        {
            if (!__instance.IsServer)
                return;

            UnlockEmotesLocal(ConfigSync.instance.syncUnlockEverything ? EmotesManager.allUnlockableEmotes : EmotesManager.complementaryEmotes);
            SyncManager.RotateEmoteSelectionServerRpc(TerminalPatcher.emoteStoreSeed);
        }


        [HarmonyPatch(typeof(StartOfRound), "StartGame")]
        [HarmonyPostfix]
        public static void ResetOverrideSeedFlag(StartOfRound __instance)
        {
            __instance.overrideRandomSeed = false;
        }


        [HarmonyPatch(typeof(StartOfRound), "ResetShip")]
        [HarmonyPostfix]
        public static void ResetEmotesOnShipReset(StartOfRound __instance)
        {
            if (!ConfigSync.instance.syncUnlockEverything)
                ResetProgressLocal();
            if (NetworkManager.Singleton.IsServer)
                SyncManager.RotateEmoteSelectionServerRpc();
        }


        public static void ResetProgressLocal(bool forceResetAll = false)
        {
            Log("Resetting progress.");
            if (!ConfigSync.instance.syncPersistentUnlocks || forceResetAll)
                ResetEmotesLocal();
            if (!ConfigSync.instance.syncPersistentEmoteCredits || forceResetAll)
            {
                TerminalPatcher.currentEmoteCredits = ConfigSync.instance.syncStartingEmoteCredits;
                var usernames = new List<string>(TerminalPatcher.currentEmoteCreditsByPlayer.Keys);
                foreach (string username in usernames)
                    TerminalPatcher.currentEmoteCreditsByPlayer[username] = ConfigSync.instance.syncStartingEmoteCredits;
            }
            TerminalPatcher.emoteStoreSeed = 0;
        }


        [HarmonyPatch(typeof(StartOfRound), "SyncShipUnlockablesServerRpc")]
        [HarmonyPostfix]
        public static void SyncUnlockedEmotesWithClients(StartOfRound __instance)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;
            if (ConfigSync.instance.syncUnlockEverything)
                return;

            if (ConfigSync.instance.syncShareEverything)
            {
                Log("Syncing unlocked emotes with clients.");
                SyncManager.SendOnUnlockEmoteUpdateMulti(TerminalPatcher.currentEmoteCredits);
            }
            else
            {
                HashSet<ulong> syncWithClients = new HashSet<ulong>();
                foreach (var playerController in StartOfRound.Instance.allPlayerScripts)
                {
                    if (playerController.actualClientId != 0 && playerController.playerSteamId != 0)
                        syncWithClients.Add(playerController.actualClientId); // Prevent duplicates
                }
                foreach (var clientId in syncWithClients)
                    SyncManager.ServerSendSyncToClient(clientId);
            }
        }


        public static bool IsEmoteUnlocked(string emoteName)
        {
            if (EmotesManager.allUnlockableEmotesDict.TryGetValue(emoteName, out var emote))
                return IsEmoteUnlocked(emote);
            return false;
        }


        public static bool IsEmoteUnlocked(UnlockableEmote emote)
        {
            if (emote == null)
                return false;

            if (emote.emoteSyncGroup != null && emote.emoteSyncGroup.Count > 0)
            {
                foreach (var otherEmote in emote.emoteSyncGroup)
                {
                    if (unlockedEmotes.Contains(otherEmote))
                        return true;
                }
            }
            return unlockedEmotes.Contains(emote);
        }


        public static List<UnlockableEmote> GetUnlockedEmotes(PlayerControllerB playerController) => GetUnlockedEmotes(playerController != null ? playerController.playerUsername : "");
        public static List<UnlockableEmote> GetUnlockedEmotes(string playerUsername)
        {
            if (unlockedEmotesByPlayer.TryGetValue(playerUsername, out var unlockedEmotes))
                return unlockedEmotes;
            return null;
        }


        public static void UnlockEmotesLocal(IEnumerable<UnlockableEmote> emotes, string playerUsername = "")
        {
            foreach (var emote in emotes)
                UnlockEmoteLocal(emote, playerUsername);
        }
        public static void UnlockEmoteLocal(int emoteId, string playerUsername = "") => UnlockEmoteLocal(emoteId >= 0 && emoteId < EmotesManager.allUnlockableEmotes.Count ? EmotesManager.allUnlockableEmotes[emoteId] : null, playerUsername);
        public static void UnlockEmoteLocal(UnlockableEmote emote, string playerUsername = "")
        {
            if (emote == null)
                return;

            var _unlockedEmotes = unlockedEmotes;
            if (playerUsername != "" && !ConfigSync.instance.syncShareEverything && playerUsername != localPlayerUsername && !unlockedEmotesByPlayer.TryGetValue(playerUsername, out _unlockedEmotes))
                return;

            // Check if one of the emotes in the sync group is already unlocked
            if (IsEmoteUnlocked(emote))
                return;
            if (emote.emoteSyncGroup != null)
            {
                foreach (var syncEmote in emote.emoteSyncGroup)
                {
                    if (_unlockedEmotes.Contains(syncEmote))
                        return;
                }
            }
            if (!_unlockedEmotes.Contains(emote))
                _unlockedEmotes.Add(emote);

            if (_unlockedEmotes == unlockedEmotes)
            {
                if (!emote.complementary)
                {
                    if (emote.rarity == 3 && !unlockedEmotesTier3.Contains(emote))
                        unlockedEmotesTier3.Add(emote);
                    else if (emote.rarity == 2 && !unlockedEmotesTier2.Contains(emote))
                        unlockedEmotesTier2.Add(emote);
                    else if (emote.rarity == 1 && !unlockedEmotesTier1.Contains(emote))
                        unlockedEmotesTier1.Add(emote);
                    else if (emote.rarity == 0 && !unlockedEmotesTier0.Contains(emote))
                        unlockedEmotesTier0.Add(emote);
                }

                if (EmotesManager.allFavoriteEmotes.Contains(emote.emoteName) && !unlockedFavoriteEmotes.Contains(emote))
                    unlockedFavoriteEmotes.Add(emote);
            }
        }


        public static void ResetEmotesLocal()
        {
            Log("Resetting unlocked emotes.");
            unlockedEmotes.Clear();
            unlockedEmotesTier0.Clear();
            unlockedEmotesTier1.Clear();
            unlockedEmotesTier2.Clear();
            unlockedEmotesTier3.Clear();

            unlockedEmotesByPlayer.Clear();
            foreach (var playerController in StartOfRound.Instance.allPlayerScripts)
            {
                if (playerController.playerSteamId != 0)
                    unlockedEmotesByPlayer.Add(playerController.playerUsername, (playerController == StartOfRound.Instance.localPlayerController || ConfigSync.instance.syncShareEverything) ? unlockedEmotes : new List<UnlockableEmote>());
            }
            UnlockEmotesLocal(ConfigSync.instance.syncUnlockEverything ? EmotesManager.allUnlockableEmotes : EmotesManager.complementaryEmotes);
            UpdateUnlockedFavoriteEmotes();
        }


        public static void UpdateUnlockedFavoriteEmotes()
        {
            unlockedFavoriteEmotes.Clear();
            foreach (string emoteName in EmotesManager.allFavoriteEmotes)
            {
                if (EmotesManager.allUnlockableEmotesDict.TryGetValue(emoteName, out var emote))
                {
                    if (emote.emoteSyncGroup != null && emote.emoteSyncGroup.Count > 0)
                        emote = emote.emoteSyncGroup[0];
                    if (IsEmoteUnlocked(emote))
                        unlockedFavoriteEmotes.Add(emote);
                }
                //else LogWarning("Error loading favorited emote. Emote does not exist. The emote has likely been temporarily removed in this update.");
            }
        }


        public static void SortUnlockedEmotes() => unlockedEmotes = unlockedEmotes.OrderBy(item => item.rarity).ThenBy(item => item.emoteName).ToList();
    }
}
