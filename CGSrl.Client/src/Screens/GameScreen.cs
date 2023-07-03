﻿using System;
using System.Collections.Generic;

using CGSrl.Client.Networking;
using CGSrl.Client.Screens.Templates;
using CGSrl.Shared.Environment;
using CGSrl.Shared.Networking;

using Lidgren.Network;

using NLog;

using PER.Abstractions;
using PER.Abstractions.Audio;
using PER.Abstractions.Input;
using PER.Abstractions.Rendering;
using PER.Abstractions.Resources;
using PER.Abstractions.Screens;
using PER.Common.Effects;
using PER.Util;

using PRR.UI;
using PRR.UI.Resources;

namespace CGSrl.Client.Screens;

public class GameScreen : LayoutResource, IScreen, IUpdatable {
    public const string GlobalId = "layouts/game";

    private const float MessageFadeInTime = 0.5f;
    private const double MessageStayTime = 60d;
    private const float MessageFadeOutTime = 5f;
    private const int MaxMessageHistory = 100;

    private static readonly Logger logger = LogManager.GetCurrentClassLogger();

    protected override IRenderer renderer => Core.engine.renderer;
    protected override IInput input => Core.engine.input;
    protected override IAudio audio => Core.engine.audio;

    protected override string layoutName => "game";

    private bool _connecting;
    private bool _joining;

    private readonly IResources _resources;
    private GameClient? _client;
    private SyncedLevel? _level;

    private Text? _loadingTextCenter;
    private Text? _loadingTextBottom;
    private ProgressBar? _loadingProgressCenter;
    private ProgressBar? _loadingProgressBottom;
    private string _loadingTextCenterFormat = "{0}";
    private string _loadingTextBottomFormat = "{0}";

    private ListBox<PlayerObject>? _players;
    private ListBox<ChatMessage>? _messages;
    private InputField? _chatInput;
    private readonly List<string> _messageHistory = new();

    private Text? _interactableText;
    private string _interactableFormat = "{0}";

    private Text? _infoText;
    private string _infoFormat = "{0} {1} {2}";

    private Type _spawnerCurrent;

    private bool _prevEscapePressed;
    private bool _prevTPressed;
    private bool _prevUpPressed;
    private bool _prevDownPressed;

    private bool _prevLeftPressed;
    private bool _prevRightPressed;

    public GameScreen(IResources resources) {
        _resources = resources;
        resources.TryAddResource(PlayerListTemplate.GlobalId, new PlayerListTemplate());
        resources.TryAddResource(ChatMessageListTemplate.GlobalId, new ChatMessageListTemplate());
        _spawnerCurrent = typeof(WallObject);
    }

    public override void Preload() {
        base.Preload();
        AddDependency<PlayerListTemplate>(PlayerListTemplate.GlobalId);
        AddDependency<ChatMessageListTemplate>(ChatMessageListTemplate.GlobalId);

        AddElement<ProgressBar>("loading.progress.center");
        AddElement<Text>("loading.text.center");
        AddElement<ProgressBar>("loading.progress.bottom");
        AddElement<Text>("loading.text.bottom");
        AddElement<ListBox<PlayerObject>>("players");
        AddElement<ListBox<ChatMessage>>("chat.messages");
        AddElement<InputField>("chat.input");
        AddElement<Text>("interactablePrompt");
        AddElement<Text>("info");
        AddElement<Button>("spawner.objects.floor");
        AddElement<Button>("spawner.objects.wall");
        AddElement<Button>("spawner.objects.box");
        AddElement<Button>("spawner.objects.effect");
        AddElement<Button>("spawner.objects.ice");
        AddElement<Button>("spawner.objects.message");
        AddElement<Button>("spawner.objects.grass");
        AddElement<Button>("spawner.objects.bomb");
        AddElement<InputField>("spawner.effect");
    }

    public override void Load(string id) {
        base.Load(id);

        _loadingTextCenter = GetElement<Text>("loading.text.center");
        _loadingTextBottom = GetElement<Text>("loading.text.bottom");
        _loadingProgressCenter = GetElement<ProgressBar>("loading.progress.center");
        _loadingProgressBottom = GetElement<ProgressBar>("loading.progress.bottom");

        _loadingTextCenter.enabled = false;
        _loadingProgressCenter.enabled = false;
        _loadingTextBottom.enabled = false;
        _loadingProgressBottom.enabled = false;

        _loadingTextCenterFormat = _loadingTextCenter.text ?? _loadingTextCenterFormat;
        _loadingTextBottomFormat = _loadingTextBottom.text ?? _loadingTextBottomFormat;

        _players = GetElement<ListBox<PlayerObject>>("players");
        _messages = GetElement<ListBox<ChatMessage>>("chat.messages");

        _chatInput = GetElement<InputField>("chat.input");
        _chatInput.onSubmit += (_, _) => {
            SendChatMessage();
            _chatInput.value = null;
        };
        _chatInput.onCancel += (_, _) => {
            _chatInput.value = null;
        };

        _interactableText = GetElement<Text>("interactablePrompt");
        _interactableFormat = _interactableText.text ?? _interactableFormat;

        _infoText = GetElement<Text>("info");
        _infoFormat = _infoText.text ?? _infoFormat;

        GetElement<Button>("spawner.objects.floor").onClick += (_, _) => _spawnerCurrent = typeof(FloorObject);
        GetElement<Button>("spawner.objects.wall").onClick += (_, _) => _spawnerCurrent = typeof(WallObject);
        GetElement<Button>("spawner.objects.box").onClick += (_, _) => _spawnerCurrent = typeof(BoxObject);
        GetElement<Button>("spawner.objects.effect").onClick += (_, _) => _spawnerCurrent = typeof(EffectObject);
        GetElement<Button>("spawner.objects.ice").onClick += (_, _) => _spawnerCurrent = typeof(IceObject);
        GetElement<Button>("spawner.objects.message").onClick += (_, _) => _spawnerCurrent = typeof(MessageObject);
        GetElement<Button>("spawner.objects.grass").onClick += (_, _) => _spawnerCurrent = typeof(GrassObject);
        GetElement<Button>("spawner.objects.bomb").onClick += (_, _) => _spawnerCurrent = typeof(BombObject);

        _level = new SyncedLevel(true, renderer, input, audio, _resources, new Vector2Int(16, 16));
        _level.objectAdded += obj => {
            if(obj is PlayerObject player)
                _players?.Add(player);
        };
        _level.objectRemoved += obj => {
            if(obj is PlayerObject player)
                _players?.Remove(player);
        };

        _client = new GameClient(_level, _messages);
        _client.onConnect += Connected;
        _client.onJoin += Joined;
        _client.onDisconnect += Disconnected;
    }

    public override void Unload(string id) {
        base.Unload(id);
        _client?.Finish();
        _client = null;
        _level = null;
    }

    public void Connect(string address, string username, string displayName) {
        if(_client is null)
            return;

        string host = "127.0.0.1";
        int port = 12420;
        if(address.Length > 0) {
            string[] parts = address.Split(':');
            host = parts[0];
            if(parts.Length >= 2 && int.TryParse(parts[1], out int parsedPort))
                port = parsedPort;
        }

        if(_loadingTextCenter is not null)
            _loadingTextCenter.enabled = true;
        if(_loadingProgressCenter is not null)
            _loadingProgressCenter.enabled = true;

        _client.Connect(host, port, username, displayName);
        _connecting = true;
    }

    private void Connected() {
        _connecting = false;
        _joining = true;
        if(_loadingTextCenter is null || _loadingProgressCenter is null ||
            _loadingTextBottom is null || _loadingProgressBottom is null)
            return;
        _loadingTextCenter.enabled = false;
        _loadingProgressCenter.enabled = false;
        _loadingTextBottom.enabled = true;
        _loadingProgressBottom.enabled = true;
    }

    private void Joined() {
        _joining = false;
        if(_loadingTextCenter is null || _loadingProgressCenter is null ||
            _loadingTextBottom is null || _loadingProgressBottom is null)
            return;
        _loadingTextCenter.enabled = false;
        _loadingProgressCenter.enabled = false;
        _loadingTextBottom.enabled = false;
        _loadingProgressBottom.enabled = false;
    }

    private void Disconnected(string reason, bool isError) {
        _level?.Reset();
        _players?.Clear();
        _messages?.Clear();
        _connecting = false;
        _joining = false;
        if(isError)
            SwitchToMainMenuWithError(reason);
        else
            SwitchToMainMenu();
    }

    public void Open() {
        if(_loadingProgressCenter is null || _loadingProgressBottom is null)
            return;
        _loadingProgressCenter.value = 0f;
        _loadingProgressBottom.value = 0f;
    }

    public void Close() {
        if(_loadingTextCenter is null || _loadingProgressCenter is null ||
            _loadingTextBottom is null || _loadingProgressBottom is null)
            return;
        _loadingTextCenter.enabled = false;
        _loadingProgressCenter.enabled = false;
        _loadingTextBottom.enabled = false;
        _loadingProgressBottom.enabled = false;
    }

    public void Update(TimeSpan time) {
        bool prevBlock = input.block;
        bool block = (_chatInput?.typing ?? false) || _connecting || _joining;

        if(_client is not null && _level is not null) {
            input.block = block;
            _client.ProcessMessages(Core.engine.updateInterval > TimeSpan.Zero ? Core.engine.updateInterval :
                Core.engine.frameTime.averageFrameTime);
            UpdatePlayerList();
            UpdateChatMessageList();
            UpdateInteractablePrompt();
            UpdateInfoText();
            _level.Update(time);
            input.block = prevBlock;
        }

        if(_connecting)
            UpdateConnectingProgress();
        else if(_joining)
            UpdateJoiningProgress();

        // ReSharper disable once ForCanBeConvertedToForeach
        for(int i = 0; i < elementList.Count; i++)
            elementList[i].Update(time);

        UpdateInput(block);
    }

    private void UpdateConnectingProgress() {
        if(_loadingTextCenter is null || _loadingProgressCenter is null ||
            _loadingTextBottom is null || _loadingProgressBottom is null ||
            _client is null)
            return;
        string text = _client.peer.ConnectionStatus switch {
            NetConnectionStatus.None or NetConnectionStatus.Disconnected => "Connecting...",
            NetConnectionStatus.InitiatedConnect => "Waiting for response...",
            NetConnectionStatus.ReceivedInitiation => "huh ????",
            NetConnectionStatus.RespondedAwaitingApproval => "Waiting for approval...",
            NetConnectionStatus.RespondedConnect => "huh 2 ???",
            NetConnectionStatus.Connected => "Connected",
            NetConnectionStatus.Disconnecting => "Disconnecting...",
            _ => "Unknow..."
        };
        float progress = _client.peer.ConnectionStatus switch {
            NetConnectionStatus.None or NetConnectionStatus.Disconnected => 0f / 3f,
            NetConnectionStatus.InitiatedConnect => 1f / 3f,
            NetConnectionStatus.RespondedAwaitingApproval => 2f / 3f,
            NetConnectionStatus.Connected => 3f / 3f,
            _ => 0f
        };
        _loadingTextCenter.text = string.Format(_loadingTextCenterFormat, text);
        _loadingTextBottom.text = string.Format(_loadingTextBottomFormat, text);
        _loadingProgressCenter.value = progress;
        _loadingProgressBottom.value = progress;
    }

    private void UpdateJoiningProgress() {
        if(_loadingTextCenter is null || _loadingProgressCenter is null ||
            _loadingTextBottom is null || _loadingProgressBottom is null ||
            _client is null)
            return;
        int received = _client.totalJoinedObjectCount - _client.leftJoinedObjectCount;
        int total = _client.totalJoinedObjectCount;
        string text = $"Receiving objects... ({received}/{total})";
        _loadingTextCenter.text = string.Format(_loadingTextCenterFormat, text);
        _loadingTextBottom.text = string.Format(_loadingTextBottomFormat, text);
        float progress = Math.Clamp(received / (float)total, 0f, 1f);
        if(!float.IsNormal(progress))
            progress = 0f;
        _loadingProgressCenter.value = progress;
        _loadingProgressBottom.value = progress;
    }

    private void UpdateInput(bool block) {
        if(_chatInput?.typing ?? false)
            UpdateChatHistoryInput();

        if(block) {
            _prevEscapePressed = input.KeyPressed(KeyCode.Escape);
            _prevTPressed = input.KeyPressed(KeyCode.T);
            _prevLeftPressed = input.MouseButtonPressed(MouseButton.Left);
            _prevRightPressed = input.MouseButtonPressed(MouseButton.Right);
            return;
        }

        UpdateExitHotkey();

        if(_connecting || _joining)
            return;

        UpdateChatHotkey();

        if(_chatInput?.currentState == ClickableElement.State.Clicked) {
            _prevLeftPressed = input.MouseButtonPressed(MouseButton.Left);
            _prevRightPressed = input.MouseButtonPressed(MouseButton.Right);
            return;
        }

        UpdateSpawner();
    }

    private void UpdateChatHistoryInput() {
        if(_chatInput is null)
            return;

        bool upPressed = input.KeyPressed(KeyCode.Up);
        bool up = !_prevUpPressed && upPressed;
        _prevUpPressed = upPressed;

        bool downPressed = input.KeyPressed(KeyCode.Down);
        bool down = !_prevDownPressed && downPressed;
        _prevDownPressed = downPressed;

        if(!up && !down)
            return;

        int index = _messageHistory.IndexOf(_chatInput.value ?? "") + (up ? -1 : 1);

        while(index >= _messageHistory.Count) index -= _messageHistory.Count + 1;
        while(index < -1) index += _messageHistory.Count + 1;

        _chatInput.value = index >= 0 && index < _messageHistory.Count ? _messageHistory[index] : null;
        _chatInput.cursor = _chatInput.value?.Length ?? 0;
    }

    private void RemoveOldHistory() {
        for(int i = 0; i < _messageHistory.Count - MaxMessageHistory; i++)
            _messageHistory.RemoveAt(i);
    }

    private void UpdateExitHotkey() {
        bool escapePressed = input.KeyPressed(KeyCode.Escape);
        if(!_prevEscapePressed && escapePressed) {
            if(_client is null)
                SwitchToMainMenu();
            else
                _client.Disconnect();
        }
        _prevEscapePressed = escapePressed;
    }

    private void UpdateChatHotkey() {
        bool tPressed = input.KeyPressed(KeyCode.T);
        if(!_prevTPressed && tPressed && _chatInput is not null)
            _chatInput.StartTyping();
        _prevTPressed = tPressed;
    }

    private void UpdateSpawner() {
        if(_level is null || _client is null)
            return;

        if(input.mousePosition is { x: >= 100, y: <= 10 })
            return;

        bool leftPressed = input.MouseButtonPressed(MouseButton.Left);
        bool rightPressed = input.MouseButtonPressed(MouseButton.Right);

        if(!_prevLeftPressed && leftPressed &&
            !_level.HasObjectAt(_level.ScreenToLevelPosition(input.mousePosition), _spawnerCurrent))
            CreateCurrentSpawnerObject();
        if(!_prevRightPressed && rightPressed)
            RemoveCurrentObject();

        _prevLeftPressed = leftPressed;
        _prevRightPressed = rightPressed;
    }

    private void CreateCurrentSpawnerObject() {
        if(_level is null || _client is null)
            return;

        if(Activator.CreateInstance(_spawnerCurrent) is not SyncedLevelObject newObj)
            return;

        newObj.position = _level.ScreenToLevelPosition(input.mousePosition);

        if(newObj is EffectObject effectObject) {
            effectObject.effect = GetElement<InputField>("spawner.effect").value ?? "none";
        }

        NetOutgoingMessage msg = _client.peer.CreateMessage(SyncedLevelObject.PreallocSize);
        msg.Write((byte)CtsDataType.AddObject);
        newObj.WriteTo(msg);
        _client.peer.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, 0);
    }

    private void RemoveCurrentObject() {
        if(_client is null || _level is null ||
            !_level.TryGetObjectAt(_level.ScreenToLevelPosition(input.mousePosition), out SyncedLevelObject? obj) &&
            obj is not PlayerObject)
            return;

        NetOutgoingMessage msg = _client.peer.CreateMessage(17);
        msg.Write((byte)CtsDataType.RemoveObject);
        msg.Write(obj.id);
        _client.peer.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, 0);
    }

    private void UpdatePlayerList() {
        if(_players is null)
            return;
        foreach(PlayerObject player in _players.items) {
            if(player.text is not Text text) {
                logger.Warn($"{player.username} doesnt have text????");
                continue;
            }
            player.highlighted = input.mousePosition.InBounds(text.bounds);
            if(!player.pingDirty)
                continue;
            _players.UpdateItem(player);
            player.pingDirty = false;
        }
    }

    private void UpdateChatMessageList() {
        if(_messages is null)
            return;
        foreach(ChatMessage message in _messages.items)
            UpdateChatMessage(message);
    }
    private void UpdateChatMessage(ChatMessage message) {
        if(_messages is null)
            return;
        if(message.element is null) {
            logger.Warn("message doesnt have element????");
            return;
        }
        if(message.player is not null)
            message.player.highlighted = message.player.highlighted ||
                input.mousePosition.InBounds(_messages.bounds) &&
                input.mousePosition.InBounds(message.element.bounds);
        if(message.element.effect is not FadeEffect { fading: false } fade)
            return;
        if(NetTime.Now - message.time >= MessageStayTime) {
            ChatMessage msg = message;
            fade.Start(MessageFadeOutTime, float.PositiveInfinity, () => _messages.Remove(msg));
        }
        else if(message.isNew)
            fade.Start(0f, MessageFadeInTime, message.fadeOutCallback);
    }

    private void SendChatMessage() {
        if(_client is null || _chatInput is null || string.IsNullOrEmpty(_chatInput.value))
            return;

        NetOutgoingMessage msg = _client.peer.CreateMessage();
        msg.Write((byte)CtsDataType.ChatMessage);
        msg.WriteTime(false);
        msg.Write(_chatInput.value);
        _client.peer.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, 0);

        // remove the current message so that if it's already in the history
        // it's moved to the end of the history
        _messageHistory.Remove(_chatInput.value);
        _messageHistory.Add(_chatInput.value);
        RemoveOldHistory();
    }

    private void UpdateInteractablePrompt() {
        if(_interactableText is null || _level is null)
            return;
        switch(PlayerObject.currentInteractable) {
            case null:
                _interactableText.text = string.Empty;
                _interactableText.position = new Vector2Int(-1, -1);
                break;
            case SyncedLevelObject obj:
                _interactableText.text = string.Format(_interactableFormat, PlayerObject.currentInteractable.prompt);
                _interactableText.position = _level.LevelToScreenPosition(obj.position + new Vector2Int(1, -1));
                break;
        }
    }

    private void UpdateInfoText() {
        if(_infoText is null || _level is null)
            return;
        _infoText.text = string.Format(_infoFormat,
            input.mousePosition,
            _level.ScreenToCameraPosition(input.mousePosition),
            _level.ScreenToLevelPosition(input.mousePosition),
            _level.ScreenToChunkPosition(input.mousePosition));
    }

    private static void SwitchToMainMenuWithError(string error) {
        if(Core.engine.resources.TryGetResource(MainMenuScreen.GlobalId, out MainMenuScreen? screen))
            Core.engine.screens.SwitchScreen(screen, () => {
                screen.ShowConnectionError(error);
                return true;
            });
    }

    private static void SwitchToMainMenu() {
        if(Core.engine.resources.TryGetResource(MainMenuScreen.GlobalId, out MainMenuScreen? screen))
            Core.engine.screens.SwitchScreen(screen);
    }
}