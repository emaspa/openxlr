import QtQuick
import QtQuick.Controls
import QtTest
import org.kde.plasma.private.volume

Item {
    width: 400
    height: 160
    GlobalConfig { id: settings }
    CheckBox {
        id: boost
        anchors.centerIn: parent
        text: "Raise maximum volume"
        checked: settings.raiseMaximumVolume
        onToggled: { settings.raiseMaximumVolume = checked; settings.save(); }
    }
    TestCase {
        name: "PlasmaVolumeRange"
        when: windowShown
        property string endpoint: "@ENDPOINT@"
        function request(path, post) {
            let reply = null;
            let done = false;
            const xhr = new XMLHttpRequest();
            xhr.onreadystatechange = function() {
                if (xhr.readyState === XMLHttpRequest.DONE) {
                    if (xhr.status === 200) reply = xhr.responseText;
                    done = true;
                }
            };
            xhr.open(post ? "POST" : "GET", endpoint + path);
            xhr.send();
            tryVerify(function() { return done; }, 5000, "Acceptance endpoint did not reply");
            verify(reply !== null, "Acceptance endpoint rejected the request");
            return reply;
        }
        function phase(value) {
            tryVerify(function() { return Number(request("/phase", false)) >= value; }, 15000);
        }
        function event(name) { request("/" + name, true); }
        function test_bidirectionalRange() {
            compare(boost.checked, false);
            event("ready");
            tryCompare(boost, "checked", true, 15000);
            event("enabled");
            phase(2);
            mouseClick(boost);
            compare(boost.checked, false);
            event("disabled");
            phase(3);
            mouseClick(boost);
            compare(boost.checked, true);
            event("reenabled");
            phase(4);
            mouseClick(boost);
            compare(boost.checked, false);
            event("hidden-disabled");
            phase(5);
            mouseClick(boost);
            compare(boost.checked, true);
            event("reopen-enabled");
            phase(6);
        }
    }
}
