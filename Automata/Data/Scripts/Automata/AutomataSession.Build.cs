using Sandbox.ModAPI;
using VRage.Utils;

using Automata.Network;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata
{
    /// <summary>
    /// Debug mode (the server's LogLevel includes Debug): which build of the mod is loaded, announced once after load.
    /// MOD_VERSION_DEBUG (AutomataSession.ModVersion.cs) is written into the deployed copies by mod_version_debug.ps1:
    /// "fingerprint time files, branch@commit - repo build / not the repository's scripts".
    ///  - Single player / host: its own build in the chat.
    ///  - Multiplayer client: sends its build to the server (BuildVersionRequestPacket); a server in debug mode answers
    ///    with its own (BuildVersionPacket) and logs the client's; the client shows both and whether they differ.
    ///  - No answer (server not in debug mode): nothing shown.
    /// </summary>
    public partial class AutomataSession
    {
        #region UI strings
        private static readonly MyStringId StrBuildLocal = MyStringId.GetOrCompute("Debug build: {0}");
        private static readonly MyStringId StrBuildServerClient = MyStringId.GetOrCompute("Debug build - server: {0} | this client: {1}");
        private static readonly MyStringId StrBuildsDiffer = MyStringId.GetOrCompute("The server and this client run different builds of the mod.");
        private static readonly MyStringId StrBuildUnversioned = MyStringId.GetOrCompute("unversioned (not deployed by mod_version_debug.ps1)");
        #endregion
        #region Timing
        private const int BUILD_ANNOUNCE_DELAY_TICKS = 5 * 60;   // after the local player appears: the chat is up
        #endregion
        private bool buildAnnounced;
        private int buildPlayerFrame = -1;

        internal static string BuildLabel(string version)
        {
            return string.IsNullOrEmpty(version) ? Loc.T(StrBuildUnversioned) : version;
        }

        // The fingerprint: the first word ("" when unversioned)
        private static string BuildHash(string version)
        {
            if (string.IsNullOrEmpty(version)) return "";
            int i = version.IndexOf(' ');
            return i < 0 ? version : version.Substring(0, i);
        }

        // UpdateBeforeSimulation: once per session, a few seconds after the local player appears (not on a dedicated server)
        private void UpdateBuildAnnounce()
        {
            if (buildAnnounced) return;
            if (MyAPIGateway.Utilities.IsDedicated) { buildAnnounced = true; return; }
            if (MyAPIGateway.Session.Player == null) return;
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            if (buildPlayerFrame < 0) { buildPlayerFrame = now; return; }
            if (now - buildPlayerFrame < BUILD_ANNOUNCE_DELAY_TICKS) return;
            buildAnnounced = true;
            if (MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer)
            {
                if (Log.DebugEnabled) MyAPIGateway.Utilities.ShowMessage(MOD_NAME, Loc.F(StrBuildLocal, BuildLabel(MOD_VERSION_DEBUG)));
                return;
            }
            Net?.SendToServer(new BuildVersionRequestPacket { ClientVersion = MOD_VERSION_DEBUG ?? "" });
        }

        /// <summary>Server: a client loaded (BuildVersionRequestPacket). Debug mode: logged, and the server's build sent back.</summary>
        internal static void ServerBuildRequested(string clientVersion, ulong steamId)
        {
            if (!Log.DebugEnabled) return;
            Log.Debug("Client {0} loaded build: {1}", steamId, BuildLabel(clientVersion));
            Instance?.Net?.SendToPlayer(new BuildVersionPacket { ServerVersion = MOD_VERSION_DEBUG ?? "" }, steamId);
        }

        /// <summary>Client: the server's answer (it only answers in debug mode).</summary>
        internal static void AnnounceBuilds(string serverVersion)
        {
            MyAPIGateway.Utilities.ShowMessage(MOD_NAME, Loc.F(StrBuildServerClient, BuildLabel(serverVersion), BuildLabel(MOD_VERSION_DEBUG)));
            if (BuildHash(serverVersion) != BuildHash(MOD_VERSION_DEBUG)) MyAPIGateway.Utilities.ShowMessage(MOD_NAME, Loc.T(StrBuildsDiffer));
        }
    }
}
