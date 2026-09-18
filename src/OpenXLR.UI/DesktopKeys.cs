using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Global keys live with the UI, including while it is hidden in the tray.</summary>
internal sealed class DesktopKeys(DaemonClient client) : IDisposable
{
    private readonly SemaphoreSlim _configure = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private IDisposable? _activated, _closed;
    private int _invoking;
    private readonly Queue<(KeyAction Action, Func<bool> IsActive)> _pending = new();
    internal string Status { get; private set; } = "Desktop keys are disabled.";
    internal event Action? Changed;

    internal Task StartAsync() => ConfigureAsync(DesktopKeySettings.Load(), save: false);

    internal async Task ConfigureAsync(DesktopKeySettings settings, bool save = true)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!await _configure.WaitAsync(0))
        {
            // A configuration can sit in the desktop's permission dialog for
            // a while. Queue this one behind it rather than dropping it: the
            // window's Apply stays disabled until it has run.
            SetStatus("Waiting for the previous configuration to finish...");
            try { await _configure.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) { return; }
        }
        bool replacing = false;
        try
        {
            settings = settings.Normalize();
            if (save) settings.Save(); // a failed save leaves the running session alone
            replacing = true;
            Stop();
            if (!settings.Enabled) { SetStatus("Desktop keys are disabled."); return; }
            SetStatus("Connecting to the desktop...");
            var connection = new DBusConnection(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? DBusAddress.Session ?? throw new InvalidOperationException("No session bus is available."));
            _connection = connection;
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            _ = ObserveConnectionAsync(connection);
            var bus = new DesktopBus(connection);
            connection.AddMethodHandler(new KWinFocus(bus));
            await connection.RequestNameAsync(KWinFocus.Service).WaitAsync(TimeSpan.FromSeconds(3), _lifetime.Token);
            var actions = Actions(settings);
            if (actions.Count == 0)
            {
                SetStatus("OpenDeck focus routing is enabled. Select channels below to assign PC shortcuts.");
                return;
            }
            var created = await bus.Request("CreateSession", "a{sv}", token => (ref MessageWriter w) =>
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token, ["session_handle_token"] = token }), _lifetime.Token);
            string session = created["session_handle"].GetString();
            int active = 0; // 0 binding, 1 active, -1 closed
            bool IsCurrent() => ReferenceEquals(_connection, connection) && !_lifetime.IsCancellationRequested;
            Func<bool> isActive = () => IsCurrent() && Volatile.Read(ref active) == 1;
            _activated = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = DesktopBus.PortalPath, Interface = DesktopBus.Shortcuts, Member = "Activated" },
                static (message, _) => { var r = message.GetBodyReader(); return (r.ReadObjectPath().ToString(), r.ReadString()); },
                notification =>
                {
                    if (!IsCurrent()) return;
                    if (notification.IsCompletion)
                    {
                        Interlocked.Exchange(ref active, -1);
                        SetStatus("Desktop connection lost. Open Desktop keys and apply to reconnect.");
                        return;
                    }
                    var (activeSession, id) = notification.Value;
                    if (isActive() && activeSession == session
                        && actions.TryGetValue(id, out KeyAction? action)) _ = InvokeAsync(action, isActive);
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            _closed = await connection.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = DesktopBus.Portal,
                Path = session, Interface = "org.freedesktop.portal.Session", Member = "Closed" },
                static (_, _) => true, _ =>
                {
                    Interlocked.Exchange(ref active, -1);
                    if (IsCurrent()) SetStatus("Shortcut session closed. Open Desktop keys and apply to reconnect.");
                }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
            await bus.Request("BindShortcuts", "oa(sa{sv})sa{sv}", token => (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                var array = w.WriteArrayStart(DBusType.Struct);
                foreach (var (id, action) in actions)
                {
                    w.WriteStructureStart(); w.WriteString(id);
                    w.WriteDictionary(new Dictionary<string, VariantValue> { ["description"] = action.Description });
                }
                w.WriteArrayEnd(array);
                w.WriteString(""); // the portal supplies its own permission/configuration window
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
            }, _lifetime.Token);
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                throw new InvalidOperationException("The desktop closed the shortcut session while it was being configured.");
            SetStatus("Desktop keys are active. Keep OpenXLR running, including in the tray.");
        }
        catch (Exception ex)
        {
            if (replacing) Stop();
            SetStatus("Desktop keys: " + ex.Message);
        }
        finally { _configure.Release(); }
    }

    internal sealed record KeyAction(string Description, Func<Task<string?>> Invoke);
    internal Dictionary<string, KeyAction> Actions(DesktopKeySettings settings)
    {
        var actions = new Dictionary<string, KeyAction>(StringComparer.Ordinal);
        foreach (string channel in settings.FocusChannels)
            actions["focus_" + channel] = new("Route focused app to " + channel, () => client.RouteFocusedAppAsync(channel));
        if (settings.OutputControls)
        {
            actions["output_up"] = new("Output volume up 5%", () => client.AdjustOutputVolumeAsync(settings.OutputDevice, .05));
            actions["output_down"] = new("Output volume down 5%", () => client.AdjustOutputVolumeAsync(settings.OutputDevice, -.05));
            actions["output_mute"] = new("Toggle output mute", () => client.ToggleOutputMuteAsync(settings.OutputDevice));
        }
        foreach (string output in settings.MainOutputs)
            actions[DesktopKeySettings.MainKey(output)] = new("Set system output to " + output, () => client.SetMainOutputAsync(output));
        return actions;
    }

    private async Task ObserveConnectionAsync(DBusConnection connection)
    {
        await connection.DisconnectedAsync().ConfigureAwait(false);
        if (ReferenceEquals(_connection, connection) && !_lifetime.IsCancellationRequested)
            SetStatus("Desktop connection lost. Open Desktop keys and apply to reconnect.");
    }

    private async Task InvokeAsync(KeyAction action, Func<bool> isActive)
    {
        bool full;
        lock (_pending)
        {
            if (!isActive()) return;
            full = _pending.Count >= 16;
            if (!full)
            {
                _pending.Enqueue((action, isActive));
                if (_invoking != 0) return;
                Volatile.Write(ref _invoking, 1);
            }
        }
        if (full)
        {
            if (isActive()) SetStatus("Desktop key queue is full; this press was not queued. Wait for the daemon before retrying.");
            return;
        }
        // One consumer preserves output-switch/volume order without losing
        // ordinary key repeats or accumulating unbounded daemon requests.
        while (true)
        {
            lock (_pending)
            {
                if (_pending.Count == 0) { Volatile.Write(ref _invoking, 0); return; }
                (action, isActive) = _pending.Dequeue();
            }
            if (!isActive()) continue;
            try
            {
                string? error = await action.Invoke();
                if (isActive()) SetStatus(error ?? "Completed: " + action.Description + ".");
            }
            catch (Exception ex) { if (isActive()) SetStatus(ex.Message); }
        }
    }

    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    private void Stop()
    {
        DBusConnection? connection = _connection;
        _connection = null; // invalidate queued callbacks before disposing their observers
        lock (_pending) _pending.Clear();
        _activated?.Dispose(); _activated = null;
        _closed?.Dispose(); _closed = null;
        // A portal session is owned by this connection and closes with it.
        connection?.Dispose();
    }
    public void Dispose() { _lifetime.Cancel(); Stop(); }
}
