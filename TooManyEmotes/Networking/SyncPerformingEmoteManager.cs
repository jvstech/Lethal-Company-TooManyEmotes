﻿using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using TooManyEmotes.Patches;
using TooManyEmotes.Config;
using UnityEngine;
using static TooManyEmotes.CustomLogging;

namespace TooManyEmotes.Networking
{
    [HarmonyPatch]
    public static class SyncPerformingEmoteManager
    {
        [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
        [HarmonyPostfix]
        public static void Init()
        {
            if (NetworkManager.Singleton.IsServer)
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("TooManyEmotes.PerformEmoteServerRpc", PerformEmoteServerRpc);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("TooManyEmotes.SyncEmoteServerRpc", SyncEmoteServerRpc);
            }
            else
            {
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("TooManyEmotes.PerformEmoteClientRpc", PerformEmoteClientRpc);
                NetworkManager.Singleton.CustomMessagingManager.RegisterNamedMessageHandler("TooManyEmotes.SyncEmoteClientRpc", SyncEmoteClientRpc);
            }
        }


        public static void SendPerformingEmoteUpdateToServer(UnlockableEmote emote, bool doNotTriggerAudio = false)
        {
            if (!NetworkManager.Singleton.IsClient || emote == null)
                return;

            Log("Sending performing emote update to server. Emote: " + emote.emoteName + " EmoteId: " + emote.emoteId);
            var writer = new FastBufferWriter(sizeof(short) + sizeof(bool), Allocator.Temp);
            writer.WriteValue((short)emote.emoteId);
            writer.WriteValue(doNotTriggerAudio);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("TooManyEmotes.PerformEmoteServerRpc", NetworkManager.ServerClientId, writer);
        }


        public static void SendSyncEmoteUpdateToServer(EmoteController emoteController, int overrideEmoteId = -1)
        {
            if (!NetworkManager.Singleton.IsClient || emoteController == null)
                return;

            Log("Sending sync emote update to server. Sync with emote controller id: " + emoteController);
            var writer = new FastBufferWriter(sizeof(ushort) + sizeof(short), Allocator.Temp);
            writer.WriteValue((ushort)emoteController.emoteControllerId);
            writer.WriteValue((short)overrideEmoteId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage("TooManyEmotes.SyncEmoteServerRpc", NetworkManager.ServerClientId, writer);
        }


        private static void PerformEmoteServerRpc(ulong clientId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            if (!SessionManager.TryGetPlayerByClientId(clientId, out var playerController) || !EmoteControllerPlayer.allPlayerEmoteControllers.TryGetValue(playerController, out var emoteController))
            {
                LogWarning("Could not handle performing emote request. Could not find emote controller for player with id: " + clientId);
                return;
            }

            reader.ReadValue(out short emoteId);
            if (emoteId < 0 || emoteId >= EmotesManager.allUnlockableEmotes.Count)
            {
                LogWarning("Could not handle performing emote request from client with id: " + clientId + ". Invalid emote id: " + emoteId + " AllUnlockableEmoteListSize: " + EmotesManager.allUnlockableEmotes.Count);
                return;
            }

            reader.ReadValue(out bool doNotTriggerAudio);
            var emote = EmotesManager.allUnlockableEmotes[emoteId];
            int overrideEmoteId = -1;
            if (emote.emoteSyncGroup != null)
                overrideEmoteId = emote.emoteSyncGroup.IndexOf(emote);

            Log("Receiving performing emote update from client: " + clientId + " Emote: " + emote.emoteName);
            if (NetworkManager.Singleton.IsClient && emoteController != EmoteControllerPlayer.emoteControllerLocal)
                emoteController.PerformEmote(emote, overrideEmoteId: overrideEmoteId, doNotTriggerAudio: doNotTriggerAudio);
            ServerSendPerformingEmoteUpdateToClients(emoteController, emote, doNotTriggerAudio);
        }


        // SYNC EMOTE
        private static void SyncEmoteServerRpc(ulong clientId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsServer)
                return;

            if (!SessionManager.TryGetPlayerByClientId(clientId, out var playerController) || !EmoteControllerPlayer.allPlayerEmoteControllers.TryGetValue(playerController, out var emoteController))
            {
                LogWarning("Could not handle sync emote request. Could not find emote controller for player with id: " + clientId);
                return;
            }

            reader.ReadValue(out ushort emoteControllerId);
            reader.ReadValue(out short overrideEmoteId);

            var syncWithEmoteController = GetEmoteControllerById(emoteControllerId);
            if (syncWithEmoteController == null)
            {
                LogWarning("Could not handle sync emote request from client with id: " + clientId + ". Failed to find emote controller with id: " + emoteControllerId);
                return;
            }

            if (syncWithEmoteController.performingEmote == null)
            {
                LogWarning("Could not handle sync emote request from client with id: " + clientId + ". Emote controller is not performing any emote.");
                return;
            }

            Log("Receiving sync emote update from client with id: " + clientId + " Sync with emote controller id: " + emoteControllerId);
            if (NetworkManager.Singleton.IsClient && emoteController != EmoteControllerPlayer.emoteControllerLocal)
                emoteController.SyncWithEmoteController(syncWithEmoteController, overrideEmoteId);
            ServerSendSyncEmoteUpdateToClients(emoteController, syncWithEmoteController, overrideEmoteId);
        }


        public static void ServerSendPerformingEmoteUpdateToClients(EmoteController emoteController, UnlockableEmote emote, bool doNotTriggerAudio = false)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                LogWarning("[ServerSendPerformingEmoteUpdateToClients] Only the server can call this method!");
                return;
            }

            if (emoteController == null || emote == null)
                return;

            var writer = new FastBufferWriter(sizeof(ushort) + sizeof(short) + sizeof(bool), Allocator.Temp);
            writer.WriteValue((ushort)emoteController.emoteControllerId);
            writer.WriteValue((short)emote.emoteId);
            writer.WriteValue(doNotTriggerAudio);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll("TooManyEmotes.PerformEmoteClientRpc", writer);
        }

        // SYNC EMOTE
        public static void ServerSendSyncEmoteUpdateToClients(EmoteController emoteController, EmoteController syncWithEmoteController, int overrideEmoteId = -1)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                LogWarning("[ServerSendSyncEmoteUpdateToClients] Only the server can call this method!");
                return;
            }

            if (emoteController == null || syncWithEmoteController == null)
                return;

            var writer = new FastBufferWriter(sizeof(ushort) * 2 + sizeof(short), Allocator.Temp);
            writer.WriteValue((ushort)emoteController.emoteControllerId);
            writer.WriteValue((ushort)syncWithEmoteController.emoteControllerId);
            writer.WriteValue((short)overrideEmoteId);
            NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll("TooManyEmotes.SyncEmoteClientRpc", writer);
        }


        private static void PerformEmoteClientRpc(ulong clientId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
                return;

            reader.ReadValue(out ushort emoteControllerId);

            // Do not update player's local emote controller
            if (EmoteControllerPlayer.emoteControllerLocal != null && emoteControllerId == EmoteControllerPlayer.emoteControllerLocal.emoteControllerId)
                return;

            var emoteController = GetEmoteControllerById(emoteControllerId);
            if (emoteController == null)
            {
                LogWarning("Could not handle performing emote request from server. Failed to find emote controller with id: " + emoteControllerId);
                return;
            }

            reader.ReadValue(out short emoteId);
            if (emoteId < 0 || emoteId >= EmotesManager.allUnlockableEmotes.Count)
            {
                LogWarning("Could not handle performing emote request from server for emote controller with id: " + emoteControllerId + ". Invalid emote id: " + emoteId + " AllUnlockableEmoteListSize: " + EmotesManager.allUnlockableEmotes.Count);
                return;
            }

            reader.ReadValue(out bool doNotTriggerAudio);
            var emote = EmotesManager.allUnlockableEmotes[emoteId];
            int overrideEmoteId = -1;
            if (emote.emoteSyncGroup != null)
                overrideEmoteId = emote.emoteSyncGroup.IndexOf(emote);
            Log("Receiving performing emote update from server for emote controller with id: " + emoteControllerId + " Emote: " + emote.emoteName);
            emoteController.PerformEmote(emote, overrideEmoteId: overrideEmoteId, doNotTriggerAudio: doNotTriggerAudio);
        }


        // SYNC EMOTE
        private static void SyncEmoteClientRpc(ulong clientId, FastBufferReader reader)
        {
            if (!NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsServer)
                return;

            reader.ReadValue(out ushort emoteControllerId);

            // Do not update player's local emote controller
            if (EmoteControllerPlayer.emoteControllerLocal != null && emoteControllerId == EmoteControllerPlayer.emoteControllerLocal.emoteControllerId)
                return;

            var emoteController = GetEmoteControllerById(emoteControllerId);
            if (emoteController == null)
            {
                LogWarning("Could not handle sync emote request from server. Failed to find emote controller with id: " + emoteControllerId);
                return;
            }

            reader.ReadValue(out ushort syncWithEmoteControllerId);
            reader.ReadValue(out short overrideEmoteId);

            var syncWithEmoteController = GetEmoteControllerById(syncWithEmoteControllerId);
            if (syncWithEmoteController == null)
            {
                LogWarning("Could not handle sync emote request from server for emote controller with id: " + emoteControllerId + ". Failed to find emote controller with id: " + emoteControllerId + " to sync with.");
                return;
            }

            if (syncWithEmoteController.performingEmote == null)
            {
                LogWarning("Could not handle sync emote request from server for emote controller with id: " + clientId + ". Emote controller is not performing any emote.");
                return;
            }

            Log("Receiving sync emote update from server for emote controller with id: " + emoteControllerId + " SyncWithEmoteControllerId: " + syncWithEmoteControllerId);
            emoteController.SyncWithEmoteController(syncWithEmoteController, overrideEmoteId);
        }


        public static EmoteController GetEmoteControllerById(ulong id)
        {
            foreach (var emoteController in EmoteController.allEmoteControllers.Values)
            {
                if (emoteController.emoteControllerId == id)
                    return emoteController;
            }
            return null;
        }
    }
}
