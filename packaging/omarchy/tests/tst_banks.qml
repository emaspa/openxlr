pragma ComponentBehavior: Bound

import QtQuick
import QtTest
import "../openxlr.mixer" as Plugin

TestCase {
    id: testCase
    name: "Omarchy strip banks"
    when: windowShown
    visible: true
    width: 800
    height: 400
    property var bank: null

    QtObject {
        id: fakeLink
        property bool ready: true
        property bool pending: false
        property var levels: ({})
        property var snapshot: null
        function send(command: var): void {
        }
    }
    Component {
        id: bankComponent
        Plugin.StripBank {
            width: 480
            title: "Channels / send mute"
            mix: "monitor"
            link: fakeLink
            foreground: "#eeeeee"
            face: "#111111"
            family: "monospace"
            entries: []
        }
    }
    function entries(count: int): var {
        var result = [];
        for (var i = 0; i < count; ++i)
            result.push({
                id: "ch" + i,
                name: "Channel " + i,
                levels: {
                    monitor: 1
                },
                mutedIn: []
            });
        return result;
    }
    function init(): void {
        bank = createTemporaryObject(bankComponent, testCase, {
            entries: entries(9)
        });
        verify(bank !== null);
    }
    function strips(item: var): var {
        var result = [];
        for (var i = 0; i < item.children.length; ++i) {
            var child = item.children[i];
            if ("entry" in child && "master" in child)
                result.push(child);
            else
                result = result.concat(strips(child));
        }
        return result;
    }
    function assertWholeStrips(): void {
        var items = strips(bank);
        for (var i = 0; i < items.length; ++i) {
            var x = items[i].mapToItem(bank, 0, 0).x;
            if (x < bank.width && x + items[i].width > 0)
                verify(x >= 0 && x + items[i].width <= bank.width, items[i].entry.name + " is clipped at " + bank.width + " px");
        }
    }
    function test_fractionalViewportNeverCutsAStrip_data(): var {
        return [
            {
                tag: "channels",
                width: 478.64
            },
            {
                tag: "masters",
                width: 293.36
            },
            {
                tag: "narrow",
                width: 151
            },
            {
                tag: "exact",
                width: 416
            }
        ];
    }
    function test_fractionalViewportNeverCutsAStrip(data: var): void {
        bank.width = data.width;
        wait(0);
        assertWholeStrips();
    }
    function test_pagingReachesEveryStripWithoutAnIntermediateOffset(): void {
        var next = findChild(bank, "nextPage");
        var previous = findChild(bank, "previousPage");
        compare(previous.enabled, false);
        var seen = [];
        do {
            wait(0);
            assertWholeStrips();
            var items = strips(bank);
            for (var i = 0; i < items.length; ++i)
                seen.push(items[i].entry.id);
            if (!next.enabled)
                break;
            mouseClick(next);
        } while (seen.length < 20)
        compare(seen, entries(9).map(function (entry) {
            return entry.id;
        }));
        compare(next.enabled, false);
        mouseClick(previous);
        wait(0);
        compare(strips(bank)[0].entry.id, "ch4");
        assertWholeStrips();
    }
    function test_resizeAndRemovalKeepTheLastPageInRange(): void {
        bank.page = 2;
        bank.width = 250;
        wait(0);
        assertWholeStrips();
        bank.entries = entries(2);
        wait(0);
        compare(bank.page, 0);
        compare(strips(bank).length, 2);
        assertWholeStrips();
        bank.entries = [];
        wait(0);
        compare(strips(bank).length, 0);
        compare(findChild(bank, "nextPage").enabled, false);
    }
}
