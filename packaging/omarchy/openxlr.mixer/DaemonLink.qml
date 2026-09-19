pragma ComponentBehavior: Bound

import QtQuick
import QtWebSockets
import Quickshell
import Quickshell.Io
import "Mixer.js" as Mixer

QtObject {
    id: root
    property bool ready: false
    property var snapshot: null
    property var levels: ({})
    property string error: ""
    property bool pending: false
    property string token: ""
    property var session: null

    function send(command: var): void {
        if (session)
            session.send(command);
    }

    function sync(): void {
        ready = session.ready;
        snapshot = session.snapshot;
        levels = session.levels;
        error = session.error;
        pending = session.pending !== "";
    }

    function arm(timer: Timer, ms: int): void {
        timer.stop();
        if (ms > 0) {
            timer.interval = ms;
            timer.start();
        }
    }

    property FileView tokenFile: FileView {
        path: Mixer.tokenPath(Quickshell.env("XDG_RUNTIME_DIR"), Quickshell.env("XDG_CONFIG_HOME"), Quickshell.env("HOME"))
        preload: false
        printErrors: false
        onLoaded: if (root.session)
            root.session.token(root.tokenFile.text())
        onLoadFailed: if (root.session)
            root.session.token("")
    }
    property WebSocket socket: WebSocket {
        url: "ws://127.0.0.1:37890/api/v1/events"
        active: false
        onStatusChanged: function (status) {
            if (!root.session)
                return;
            if (status === WebSocket.Open) {
                root.session.opened(root.token);
                root.token = "";
            } else if (status === WebSocket.Closed || status === WebSocket.Error) {
                root.session.lost();
            }
        }
        onTextMessageReceived: message => root.session.receive(message)
    }
    property Timer retry: Timer {
        onTriggered: root.session.start()
    }
    property Timer deadline: Timer {
        onTriggered: root.session.lost()
    }
    property Timer meterDeadline: Timer {
        onTriggered: root.session.metersExpired()
    }
    property Timer commandDeadline: Timer {
        onTriggered: root.session.commandExpired()
    }

    Component.onCompleted: {
        session = new Mixer.Session({
            readToken: function () {
                root.tokenFile.reload();
                // With preload off, text() starts the asynchronous read. Its
                // cached return is ignored; only onLoaded supplies the token.
                root.tokenFile.text();
            },
            connect: function (value) {
                root.token = value;
                root.socket.active = true;
            },
            send: function (value) {
                root.socket.sendTextMessage(JSON.stringify(value));
            },
            close: function () {
                root.socket.active = false;
                root.token = "";
            },
            changed: function () {
                root.sync();
            },
            retry: function (ms) {
                root.arm(root.retry, ms);
            },
            deadline: function (ms) {
                root.arm(root.deadline, ms);
            },
            meterDeadline: function (ms) {
                root.arm(root.meterDeadline, ms);
            },
            commandDeadline: function (ms) {
                root.arm(root.commandDeadline, ms);
            },
            cancelTimers: function () {
                root.retry.stop();
                root.deadline.stop();
                root.meterDeadline.stop();
                root.commandDeadline.stop();
            }
        });
        session.start();
    }
    Component.onDestruction: if (session)
        session.stop()
}
