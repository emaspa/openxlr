using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OpenXLR.UI;

/// <summary>
/// Ctrl+Z undoes and Ctrl+Shift+Z or Ctrl+Y redoes, in each window that
/// edits what the daemon's history records. The keys are heard as they
/// bubble up, and a key that came from inside a text field is left to that
/// field whether or not it used it, so a name box keeps its own undo.
/// </summary>
internal static class HistoryKeys
{
    /// <summary>What a key press asks of the history.</summary>
    internal enum Press { None, Undo, Redo }

    /// <summary>Listen on <paramref name="root"/>; <paramref name="step"/> gets true for a redo.</summary>
    public static void Attach(InputElement root, Action<bool> step)
        => root.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            Press press = Match(e.Key, e.KeyModifiers, e.Source);
            if (press == Press.None) return;
            e.Handled = true;
            step(press == Press.Redo);
        }, RoutingStrategies.Bubble);

    /// <summary>The history action a key asks for, or none when it is another key or came from a text field.</summary>
    internal static Press Match(Key key, KeyModifiers modifiers, object? source)
    {
        Press press = (key, modifiers) switch
        {
            (Key.Z, KeyModifiers.Control) => Press.Undo,
            (Key.Z, KeyModifiers.Control | KeyModifiers.Shift) => Press.Redo,
            (Key.Y, KeyModifiers.Control) => Press.Redo,
            _ => Press.None,
        };
        return press != Press.None && FromTextField(source) ? Press.None : press;
    }

    /// <summary>Whether the key came from a text box, a number box or a suggestion box, or from inside one.</summary>
    internal static bool FromTextField(object? source)
        => source is Visual visual && visual.GetSelfAndVisualAncestors()
            .Any(v => v is TextBox or NumericUpDown or AutoCompleteBox);
}
