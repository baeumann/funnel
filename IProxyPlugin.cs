using System;
using funnel_.protocol;

namespace funnel_.core;

/// <summary>
/// Das offizielle, GitHub-sichere Interface für dynamisch ladbare Plugins (.dll).
/// Jede externe Bibliothek muss dieses Interface implementieren, um vom Proxy geladen zu werden.
/// </summary>
public interface IProxyPlugin
{
    /// <summary>Der eindeutige Name des Plugins für das Terminal-Logging.</summary>
    string Name { get; }

    /// <summary>Die Versionsnummer des Plugins.</summary>
    string Version { get; }

    /// <summary>
    /// Wird aufgerufen, sobald das Plugin erfolgreich vom Core geladen wurde.
    /// </summary>
    void OnLoad();

    /// <summary>
    /// Wird aufgerufen, sobald eine neue Minecraft-Sitzung gestartet wird.
    /// Klinkt das Plugin direkt in den Live-Traffic ein.
    /// </summary>
    void OnSessionStarted(Session session);
}
