﻿using Cgsrl.Shared.Environment;
using Cgsrl.Shared.Networking;

using Lidgren.Network;

using NLog;

using PER.Abstractions.Environment;

namespace Cgsrl.Server.Networking;

public class GameServer {
    private static readonly Logger logger = LogManager.GetCurrentClassLogger();

    private readonly NetServer _peer;
    private readonly Level<SyncedLevelObject> _level;

    public GameServer(Level<SyncedLevelObject> level, int port) {
        _level = level;

        NetPeerConfiguration config = new("CGSrl") { Port = port };
        config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
        config.EnableMessageType(NetIncomingMessageType.StatusChanged);
        config.EnableMessageType(NetIncomingMessageType.Data);

        _peer = new NetServer(config);

        _level.objectAdded += obj => {
            NetOutgoingMessage msg = _peer.CreateMessage(SyncedLevelObject.PreallocSize);
            msg.Write((byte)StcDataType.ObjectAdded);
            obj.WriteTo(msg);
            _peer.SendToAll(msg, NetDeliveryMethod.ReliableOrdered, 0);
        };
        _level.objectRemoved += obj => {
            NetOutgoingMessage msg = _peer.CreateMessage(17);
            msg.Write((byte)StcDataType.ObjectRemoved);
            msg.Write(obj.id);
            _peer.SendToAll(msg, NetDeliveryMethod.ReliableOrdered, 0);
        };
        _level.objectChanged += obj => {
            NetOutgoingMessage msg = _peer.CreateMessage(SyncedLevelObject.PreallocSize);
            msg.Write((byte)StcDataType.ObjectChanged);
            obj.WriteDataTo(msg);
            _peer.SendToAll(msg, NetDeliveryMethod.ReliableOrdered, 0);
        };

        _peer.Start();
    }

    public void Finish() {
        _peer.Shutdown("Server closed");
    }

    public void ProcessMessages() {
        while(_peer.ReadMessage(out NetIncomingMessage msg)) {
            switch(msg.MessageType) {
                case NetIncomingMessageType.WarningMessage:
                    logger.Warn(msg.ReadString);
                    break;
                case NetIncomingMessageType.ErrorMessage:
                    logger.Error(msg.ReadString);
                    break;
                case NetIncomingMessageType.ConnectionApproval:
                    ProcessConnectionApproval(msg);
                    break;
                case NetIncomingMessageType.StatusChanged:
                    ProcessStatusChanged((NetConnectionStatus)msg.ReadByte(), msg.ReadString(), msg.SenderConnection);
                    break;
                case NetIncomingMessageType.Data:
                    CtsDataType type = (CtsDataType)msg.ReadByte();
                    ProcessData(type, msg);
                    break;
                default:
                    logger.Error("Unhandled message type: {}", msg.MessageType);
                    break;
            }
            _peer.Recycle(msg);
        }
    }

    private void ProcessConnectionApproval(NetIncomingMessage msg) {
        NetConnection connection = msg.SenderConnection;
        string username = msg.ReadString();
        string displayName = msg.ReadString();
        if(string.IsNullOrEmpty(username)) {
            connection.Deny("Empty username");
            return;
        }
        if(string.IsNullOrEmpty(displayName)) {
            connection.Deny("Empty display name");
            return;
        }
        if(username.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_' && c != '-')) {
            connection.Deny("Invalid username: only lowercase letters, digits, _ and - are allowed");
            return;
        }
        if(_level.objects.Values.OfType<PlayerObject>().Any(p => p.username == username)) {
            connection.Deny("Player with this username already exists");
            return;
        }

        connection.Tag = new PlayerObject { connection = connection, username = username, displayName = displayName };

        // hail message can't be too large so send it after the client connects
        connection.Approve();

        logger.Info($"[{username}] connection approved");
        SendSystemMessage($"\f1{displayName} is joining the game.");
    }

    private void ProcessStatusChanged(NetConnectionStatus status, string reason, NetConnection connection) {
        switch(status) {
            case NetConnectionStatus.Connected: {
                if(connection.Tag is not PlayerObject player) {
                    logger.Warn("Tag was not player, ignoring connect!");
                    return;
                }

                int prealloc = 1 + sizeof(int) + _level.objects.Count * SyncedLevelObject.PreallocSize;
                NetOutgoingMessage hail = _peer.CreateMessage(prealloc);
                hail.Write((byte)StcDataType.Joined);
                hail.Write(_level.objects.Count);
                foreach(SyncedLevelObject obj in _level.objects.Values)
                    obj.WriteTo(hail);
                connection.SendMessage(hail, NetDeliveryMethod.ReliableOrdered, 0);

                _level.Add(player);
                logger.Info($"[{player.username}] connected");
                SendSystemMessage($"{player.displayName} \f2joined the game.");
                SendSystemMessageTo(connection, "welcome :>"); // TODO: chat motd
                break;
            }
            case NetConnectionStatus.Disconnected: {
                if(connection.Tag is not PlayerObject player) {
                    logger.Warn("Tag was not player, ignoring disconnect!");
                    return;
                }
                _level.Remove(player);
                logger.Info($"[{player.username}] disconnected ({reason})");
                SendSystemMessage($"{player.displayName} \f2left the game. \f1({reason})");
                break;
            }
        }
    }

    private void ProcessData(CtsDataType type, NetIncomingMessage msg) {
        switch(type) {
            case CtsDataType.AddObject:
                ProcessAddObject(msg);
                break;
            case CtsDataType.RemoveObject:
                ProcessRemoveObject(msg);
                break;
            case CtsDataType.PlayerMove:
                ProcessPlayerMove(msg);
                break;
            case CtsDataType.ChatMessage:
                ProcessChatMessage(msg);
                break;
            default:
                logger.Error("Unhandled CTS data type: {}", type);
                break;
        }
    }

    private void ProcessAddObject(NetBuffer msg) {
        SyncedLevelObject obj = SyncedLevelObject.Read(msg);
        if(_level.objects.ContainsKey(obj.id)) {
            logger.Warn("Object {} (of type {}) already exists, ignoring add object!", obj.id, obj.GetType().Name);
            return;
        }
        _level.Add(obj);
    }

    private void ProcessRemoveObject(NetBuffer msg) {
        Guid id = msg.ReadGuid();
        if(!_level.objects.TryGetValue(id, out SyncedLevelObject? obj)) {
            logger.Warn("Object {} doesn't exist, ignoring remove object!", id);
            return;
        }
        if(obj is PlayerObject) {
            logger.Warn("Object {} is a player, ignoring remove object!", id);
            return;
        }
        _level.Remove(id);
    }

    private static void ProcessPlayerMove(NetIncomingMessage msg) {
        if(msg.SenderConnection.Tag is not PlayerObject player) {
            logger.Warn("Tag was not player, ignoring player move!");
            return;
        }
        player.move = msg.ReadVector2Int();
    }

    private void ProcessChatMessage(NetIncomingMessage msg) {
        if(msg.SenderConnection.Tag is not PlayerObject player) {
            logger.Warn("Tag was not player, ignoring chat message!");
            return;
        }
        NetOutgoingMessage message = _peer.CreateMessage();
        message.Write((byte)StcDataType.ChatMessage);
        message.Write(player.id);
        message.WriteTime(msg.ReadTime(false), false);
        string text = msg.ReadString();
        message.Write(text);
        _peer.SendToAll(message, NetDeliveryMethod.ReliableOrdered, 0);
        logger.Info($"[CHAT] [{player.username}] {text}");
    }

    private void SendSystemMessage(string text) {
        NetOutgoingMessage message = _peer.CreateMessage();
        message.Write((byte)StcDataType.ChatMessage);
        message.Write(Guid.Empty);
        message.WriteTime(false);
        message.Write(text);
        _peer.SendToAll(message, NetDeliveryMethod.ReliableOrdered, 0);
    }

    private void SendSystemMessageTo(NetConnection connection, string text) {
        NetOutgoingMessage message = _peer.CreateMessage();
        message.Write((byte)StcDataType.ChatMessage);
        message.Write(Guid.Empty);
        message.WriteTime(false);
        message.Write(text);
        connection.SendMessage(message, NetDeliveryMethod.ReliableOrdered, 0);
    }
}