using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace MuddyScrapPileHighlight
{
    /// <summary>
    /// Keeps the set of Muddy Scrap Pile objects that are close enough to the player.
    ///
    /// The highlight follows the mining cycle: an intact pile (mudpile, mudpile2,
    /// mudpile_beacon, mudpile_old - a Destructible) is never highlighted, but the frac
    /// chunk that takes its place after one pickaxe blow - a MineRock5 of about 30 areas,
    /// one visible fragment each - is, with a shell per area. The chunk is recognised by
    /// its MineRock5 and a frac name, a frac ancestor, the mudpile family or the spot a
    /// pile just stood on, never by name alone: the game spawns it as "___MineRock5". A
    /// chunk anywhere in range takes precedence over every pile; with OnlyChunks off, a
    /// pile stands in for the chunks only while no chunk exists at all.
    ///
    /// Piles are found through the components every one of them carries - MineRock5 for
    /// the fragments, Destructible for the intact pile - inactive ones included. Each
    /// component scan is one traversal of the loaded scenes, so only the ones the current
    /// configuration can actually highlight are run.
    /// </summary>
    internal class PileTracker
    {
        /// <summary>
        /// The highlighted prefabs. A child the game instantiates with a suffix still
        /// belongs to them, so a name starting with one of these counts as well.
        /// </summary>
        private static readonly string[] TargetNames = { "mudpile_frac", "mudpile2_frac" };

        /// <summary>Name family of the containers we search the chunks inside.</summary>
        private const string PilePrefix = "mudpile";

        private readonly ModConfig config;
        private readonly ManualLogSource log;

        /// <summary>The live set: tracked chunks in range. Swapped wholesale on every scan.</summary>
        private HashSet<GameObject> chunks = new HashSet<GameObject>();

        /// <summary>Scratch set built by a scan, then swapped with <see cref="chunks"/>.</summary>
        private HashSet<GameObject> scanBuffer = new HashSet<GameObject>();

        private readonly List<GameObject> scanList = new List<GameObject>();
        private readonly HashSet<GameObject> seenCandidates = new HashSet<GameObject>();

        /// <summary>
        /// Reused by every proximity query: one overlap returns at most this many
        /// colliders, and the buffer is never reallocated. Crowded enough to overflow only
        /// in a dense player build, where the piles are not anyway.
        /// </summary>
        private readonly Collider[] nearbyColliders = new Collider[256];
        private readonly List<Transform> subtreeScratch = new List<Transform>();
        private readonly List<GameObject> rootsScratch = new List<GameObject>();
        private readonly List<GameObject> mineRockList = new List<GameObject>();
        private readonly Dictionary<string, int> nameCounts = new Dictionary<string, int>();

        /// <summary>
        /// MineRock5 objects of the last scan, to spot the ones a pickaxe blow just
        /// spawned - the game does not name them reliably (___MineRock5 was seen).
        /// </summary>
        private HashSet<GameObject> previousMineRocks = new HashSet<GameObject>();

        private HashSet<GameObject> newMineRocks = new HashSet<GameObject>();

        /// <summary>
        /// Fragments already recognised once, kept until the game destroys them: "appeared
        /// where a pile just stood" only holds for the scan right after the blow, and the
        /// fragment carries no usable name to recognise it by afterwards (___MineRock5).
        /// </summary>
        private HashSet<GameObject> knownFragments = new HashSet<GameObject>();

        private readonly List<GameObject> staleScratch = new List<GameObject>();

        /// <summary>Where the piles stood at the last scan: a spawned chunk sits there.</summary>
        private List<Vector3> pilePositions = new List<Vector3>();

        private List<Vector3> pilePositionsBuffer = new List<Vector3>();

        /// <summary>How close a freshly spawned MineRock5 must be to a pile to count as its chunk.</summary>
        private const float SpawnTolerance = 3f;

        /// <summary>DebugLog only: the subtree of every watched pile, to spot changes.</summary>
        private Dictionary<GameObject, string> pileSignatures =
            new Dictionary<GameObject, string>();

        private readonly System.Text.StringBuilder signatureScratch =
            new System.Text.StringBuilder();

        private float nextScanTime;
        private bool announcedFirstFind;
        private bool hierarchyDumped;
        private bool loggedMissingChunks;
        private bool censusDone;
        private bool fracHunted;

        internal PileTracker(ModConfig config, ManualLogSource log)
        {
            this.config = config;
            this.log = log;
        }

        /// <summary>Chunks currently tracked and within range. Do not keep a reference across ticks.</summary>
        internal HashSet<GameObject> Chunks => chunks;

        /// <summary>
        /// Rescans when the interval elapsed. Returns true when the tracked set changed,
        /// so the caller knows the highlighters have work to do.
        /// </summary>
        internal bool Tick(float now)
        {
            if (now < nextScanTime)
            {
                return false;
            }

            nextScanTime = now + Mathf.Max(0.2f, config.ScanInterval.Value);

            Player player = Player.m_localPlayer;
            if (player == null)
            {
                // Main menu: nothing to highlight, and stale entries would keep shells
                // alive on objects the next world has destroyed.
                return Reset();
            }

            return Rescan(player);
        }

        /// <summary>Forgets everything, so the next tick starts from a clean slate.</summary>
        internal bool Reset()
        {
            bool changed = chunks.Count > 0;
            chunks.Clear();
            scanBuffer.Clear();
            nextScanTime = 0f;
            announcedFirstFind = false;
            hierarchyDumped = false;
            loggedMissingChunks = false;
            censusDone = false;
            fracHunted = false;
            pileSignatures.Clear();
            previousMineRocks.Clear();
            newMineRocks.Clear();
            knownFragments.Clear();
            staleScratch.Clear();
            pilePositions.Clear();
            pilePositionsBuffer.Clear();
            return changed;
        }

        private bool Rescan(Player player)
        {
            Vector3 origin = player.transform.position;
            float maxSqr = config.MaxDistance.Value * config.MaxDistance.Value;

            scanList.Clear();
            seenCandidates.Clear();
            mineRockList.Clear();

            // Candidates come from a proximity query, not a scene-wide search: only what
            // sits within reach of the player is touched, so the cost follows how crowded
            // the spot is instead of how much of the world is loaded. This is how the game
            // itself finds mining hits (Physics.OverlapSphere in MineRock5).
            CollectNearby(origin, maxSqr);

            scanBuffer.Clear();
            rootsScratch.Clear();
            GameObject firstRoot = null;
            int rootsInRange = 0;

            if (config.DebugLog.Value)
            {
                PurgeSignatures();
            }

            foreach (GameObject candidate in scanList)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (!candidate.name.StartsWith(PilePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if ((candidate.transform.position - origin).sqrMagnitude > maxSqr)
                {
                    continue;
                }

                if (!seenCandidates.Add(candidate))
                {
                    // Several components of the same pile matched: one descent is enough.
                    continue;
                }

                rootsInRange++;
                if (firstRoot == null)
                {
                    firstRoot = candidate;
                }

                rootsScratch.Add(candidate);
                pilePositionsBuffer.Add(candidate.transform.position);
                candidate.GetComponentsInChildren(true, subtreeScratch);

                if (config.DebugLog.Value)
                {
                    WatchSubtree(candidate, subtreeScratch);
                }

                foreach (Transform node in subtreeScratch)
                {
                    if (IsTarget(node.name))
                    {
                        scanBuffer.Add(node.gameObject);
                    }
                }

                subtreeScratch.Clear();
            }

            // A MineRock5 is what a pile becomes once struck: the fracturable chunk, its
            // ~30 areas one visible fragment each. The name is unreliable - the game was
            // seen spawning one as "___MineRock5" - so it is recognised by a frac name or
            // ancestor, by belonging to the mudpile family, or by having appeared where a
            // pile just stood.
            newMineRocks.Clear();
            PurgeStale(knownFragments);
            foreach (GameObject mineRock in mineRockList)
            {
                if (mineRock == null)
                {
                    continue;
                }

                if ((mineRock.transform.position - origin).sqrMagnitude > maxSqr)
                {
                    continue;
                }

                bool appeared = !previousMineRocks.Contains(mineRock);
                newMineRocks.Add(mineRock);

                if (appeared && config.DebugLog.Value)
                {
                    log.LogInfo(
                        $"New MineRock5 in range: {mineRock.name} at {mineRock.transform.position} "
                            + $"({DescribeLineage(mineRock.transform)}) - "
                            + $"{DescribeComponents(mineRock.transform)} - "
                            + $"m_name='{mineRock.GetComponent<MineRock5>()?.m_name}'"
                    );
                    DumpHierarchy(mineRock);
                }

                if (knownFragments.Contains(mineRock) || IsFragment(mineRock, appeared))
                {
                    // Recognised once is recognised for good, until destruction: the
                    // fragment is mined over many blows and must stay highlighted.
                    knownFragments.Add(mineRock);
                    scanBuffer.Add(mineRock);
                }
            }

            HashSet<GameObject> swappedMineRocks = previousMineRocks;
            previousMineRocks = newMineRocks;
            newMineRocks = swappedMineRocks;
            newMineRocks.Clear();

            List<Vector3> swappedPositions = pilePositions;
            pilePositions = pilePositionsBuffer;
            pilePositionsBuffer = swappedPositions;
            pilePositionsBuffer.Clear();

            // Global preference: a chunk anywhere in range replaces every pile. Only the
            // fragments a pickaxe blow leaves behind are worth highlighting, an intact pile
            // never is - the pile stands in for the chunks only while no chunk exists at
            // all, which is what OnlyChunks turns off.
            if (scanBuffer.Count == 0 && !config.OnlyChunks.Value)
            {
                foreach (GameObject root in rootsScratch)
                {
                    // An inactive pile is skipped: its shell could not be seen anyway, and
                    // one the game activates later is picked up by the next scan.
                    if (root.activeInHierarchy)
                    {
                        scanBuffer.Add(root);
                    }
                }
            }

            rootsScratch.Clear();

            ReportDiagnostics(firstRoot, rootsInRange, scanBuffer.Count);

            bool changed = !scanBuffer.SetEquals(chunks);
            if (changed && config.DebugLog.Value)
            {
                LogScan(scanBuffer, rootsInRange);
            }

            // Swap: the new set becomes live, the old one is cleared for the next scan.
            HashSet<GameObject> previous = chunks;
            chunks = scanBuffer;
            scanBuffer = previous;
            scanBuffer.Clear();

            if (!announcedFirstFind && chunks.Count > 0)
            {
                announcedFirstFind = true;
                log.LogInfo(
                    $"Tracking {chunks.Count} Muddy Scrap Pile(s) within "
                        + $"{config.MaxDistance.Value:0} m"
                );
            }

            return changed;
        }

        /// <summary>True for mudpile2_frac and any suffixed child of theirs.</summary>
        private static bool IsTarget(string name)
        {
            foreach (string target in TargetNames)
            {
                if (name.Equals(target, StringComparison.Ordinal))
                {
                    return true;
                }

                if (
                    name.Length > target.Length
                    && name.StartsWith(target, StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// DebugLog only: re-dumps a pile's hierarchy whenever it changes, which is how a
        /// pile blown into a frac chunk by one pickaxe strike - mudpile2 disappears,
        /// mudpile2_frac takes its place - gets caught on the spot, areas and all.
        /// </summary>
        private void WatchSubtree(GameObject root, List<Transform> subtree)
        {
            signatureScratch.Length = 0;
            foreach (Transform node in subtree)
            {
                signatureScratch.Append(node.name).Append('|');
            }

            string signature = signatureScratch.ToString();
            if (!pileSignatures.TryGetValue(root, out string previous))
            {
                pileSignatures[root] = signature;
                return;
            }

            if (previous == signature)
            {
                return;
            }

            pileSignatures[root] = signature;
            log.LogInfo($"Hierarchy of {root.name} changed:");
            DumpHierarchy(root);
        }

        /// <summary>Forgets the watched piles the game destroyed, signature bookkeeping only.</summary>
        private void PurgeSignatures()
        {
            bool anyStale = false;
            foreach (KeyValuePair<GameObject, string> pair in pileSignatures)
            {
                if (pair.Key == null)
                {
                    anyStale = true;
                    break;
                }
            }

            if (!anyStale)
            {
                return;
            }

            Dictionary<GameObject, string> cleaned = new Dictionary<GameObject, string>();
            foreach (KeyValuePair<GameObject, string> pair in pileSignatures)
            {
                if (pair.Key != null)
                {
                    cleaned[pair.Key] = pair.Value;
                }
            }

            pileSignatures = cleaned;
        }

        /// <summary>
        /// A MineRock5 counts as a pile fragment when it carries a frac name, sits under a
        /// frac root, belongs to the mudpile family, or appeared where a pile just stood:
        /// the game was seen spawning the chunk under a generic name (___MineRock5), so a
        /// name filter alone would miss the whole point of the mod.
        /// </summary>
        private bool IsFragment(GameObject mineRock, bool appeared)
        {
            if (IsTarget(mineRock.name))
            {
                return true;
            }

            int depth = 0;
            for (Transform up = mineRock.transform.parent; up != null && depth < 8; up = up.parent)
            {
                if (IsTarget(up.name))
                {
                    return true;
                }

                depth++;
            }

            if (mineRock.name.StartsWith(PilePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            // The frac name can also sit on a child - the game was seen building the
            // chunk under a generic root - so a newly appeared MineRock5 is checked
            // downwards once. Bounded and rare: only an unrecognised new chunk pays it.
            if (appeared && HasFracDescendant(mineRock.transform))
            {
                return true;
            }

            // The one rule that needs neither a name nor a memory: MineRock5.Awake renames
            // its own GameObject to "___MineRock5 m_meshFilter", wiping whatever the prefab
            // was called, but the m_name field the hover text shows survives - and it says
            // which pile this is. It runs for a chunk no cheaper rule claimed, so the
            // GetComponent is paid on at most a couple of objects per scan, and only until
            // the chunk is remembered. This is what catches a chunk that was already there
            // at the very first scan, where "appeared near a pile" can never fire.
            if (IsMuddyMineRock(mineRock))
            {
                return true;
            }

            return appeared && NearLastPile(mineRock.transform.position);
        }

        /// <summary>
        /// True when the chunk's own display name says it is a Muddy Scrap Pile. The field
        /// holds the English source or the localization key the game ships ("Muddy Scrap
        /// Pile", "$piece_mudpile" / "piece_mudpile"); "boue" is matched too in case a
        /// build bakes the French "Tas de boue" straight into the prefab. No ore name in
        /// the game contains "mud", so this cannot claim copper, tin or stone by mistake.
        /// </summary>
        private static bool IsMuddyMineRock(GameObject mineRock)
        {
            string name = mineRock.GetComponent<MineRock5>()?.m_name;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return name.IndexOf("mud", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("boue", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True when any descendant of the chunk carries a frac name: the game builds the
        /// chunk under a generic root (___MineRock5 was seen), so the frac name - if it
        /// survives at all - can only be found by walking down. Bounded and only called
        /// for a newly appeared, still unrecognised chunk, so the cost is a few name
        /// comparisons on at most a couple of chunks per scan.
        /// </summary>
        private bool HasFracDescendant(Transform root)
        {
            int visited = 0;
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
            {
                if (node == root)
                {
                    continue;
                }

                if (IsTarget(node.name))
                {
                    return true;
                }

                if (++visited >= 96)
                {
                    // A chunk is ~30 areas plus decorations; anything bigger is not one.
                    break;
                }
            }

            return false;
        }

        /// <summary>
        /// True when a pile stood there at the previous scan or is standing there right
        /// now, within tolerance.
        /// </summary>
        private bool NearLastPile(Vector3 position)
        {
            float sqr = SpawnTolerance * SpawnTolerance;
            foreach (Vector3 pilePosition in pilePositions)
            {
                if ((position - pilePosition).sqrMagnitude <= sqr)
                {
                    return true;
                }
            }

            // The current scan fills the buffer before this runs, while pilePositions only
            // gets it at the swap at the end of the scan. Checking both means a chunk that
            // was already in place at the very first scan still matches: on that scan
            // pilePositions is still empty, so only the buffer can see the pile beside it.
            foreach (Vector3 pilePosition in pilePositionsBuffer)
            {
                if ((position - pilePosition).sqrMagnitude <= sqr)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Forgets the entries the game destroyed: a destroyed GameObject compares equal to
        /// every other destroyed one, so stale entries must go or they would make a brand
        /// new fragment look already known.
        /// </summary>
        private void PurgeStale(HashSet<GameObject> set)
        {
            staleScratch.Clear();
            foreach (GameObject entry in set)
            {
                if (entry == null)
                {
                    staleScratch.Add(entry);
                }
            }

            foreach (GameObject entry in staleScratch)
            {
                set.Remove(entry);
            }
        }

        private static string DescribeLineage(Transform node)
        {
            List<string> names = new List<string>();
            for (Transform up = node.parent; up != null && names.Count < 5; up = up.parent)
            {
                names.Add(up.name);
            }

            return names.Count == 0 ? "no parent" : string.Join(" > ", names);
        }

        /// <summary>
        /// Fills the two candidate lists from a single proximity query: the fragment roots
        /// their own colliders point to, and the pile roots they belong to. Nothing but
        /// what is within reach of the player is looked at, so the cost tracks how crowded
        /// the spot is rather than how much of the world is loaded - this is the whole
        /// point, since a scene-wide FindObjectsByType used to run every scan.
        ///
        /// Deduplicated by list membership: a chunk answers for all ~30 of its area
        /// colliders, and these lists stay a handful long, so a linear check is free.
        /// </summary>
        private void CollectNearby(Vector3 origin, float maxSqr)
        {
            // The colliders sit on the fragment's areas and the pile's body, which reach a
            // little past the root; a few metres of slack keeps a root that is itself within
            // MaxDistance from being missed because its own collider lies outside it.
            float radius = Mathf.Sqrt(maxSqr) + 3f;
            int count = Physics.OverlapSphereNonAlloc(
                origin,
                radius,
                nearbyColliders,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide
            );

            for (int i = 0; i < count; i++)
            {
                Collider hit = nearbyColliders[i];
                if (hit == null)
                {
                    continue;
                }

                // The areas carry the colliders (MineRock5.SetupColliders) under the root
                // that holds the component, so it is looked up the chain: a couple of
                // native calls per collider, all local to the player.
                MineRock5 chunk = hit.GetComponentInParent<MineRock5>(true);
                if (chunk != null)
                {
                    GameObject root = chunk.gameObject;
                    if (!mineRockList.Contains(root))
                    {
                        mineRockList.Add(root);

                        // The chunk doubles as a pile root, exactly as the scene-wide scan
                        // collected it - the name filter in the caller decides the descent.
                        if (!scanList.Contains(root))
                        {
                            scanList.Add(root);
                        }
                    }
                }

                // The intact pile (a Destructible) is only ever a root to descend or a
                // stand-in while no chunk exists - both of which OnlyChunks turns off.
                if (!config.OnlyChunks.Value)
                {
                    Destructible pile = hit.GetComponentInParent<Destructible>(true);
                    if (pile != null && !scanList.Contains(pile.gameObject))
                    {
                        scanList.Add(pile.gameObject);
                    }
                }
            }
        }

        /// <summary>
        /// Makes the state of the search visible: the hierarchy of the first pile is dumped
        /// once under DebugLog, and once regardless of it when no chunk was found - which is
        /// exactly the case where a look at the tree is needed. Toggling the mod re-arms it.
        /// </summary>
        private void ReportDiagnostics(GameObject firstRoot, int roots, int targets)
        {
            if (firstRoot == null)
            {
                return;
            }

            if (!hierarchyDumped && (config.DebugLog.Value || targets == 0))
            {
                hierarchyDumped = true;

                if (targets == 0 && roots > 0 && !loggedMissingChunks)
                {
                    loggedMissingChunks = true;
                    log.LogWarning(
                        $"Found {roots} Muddy Scrap Pile(s) in range but no fracturable "
                            + $"chunk (MineRock5) among them - dumping the hierarchy "
                            + $"of {firstRoot.name}:"
                    );
                }

                DumpHierarchy(firstRoot);
                Census(false);

                if (config.DebugLog.Value)
                {
                    HuntFracs();
                }

                return;
            }

            if (config.DebugLog.Value && chunks.Count == 0 && targets > 0)
            {
                // The chunks just appeared (mining started): show where they came from.
                DumpHierarchy(firstRoot);
                Census(true);
                HuntFracs();
            }
        }

        /// <summary>
        /// Lists every loaded object whose name starts with "mudpile", deactivated ones
        /// included - the only way to tell a chunk the game keeps hidden from a chunk that
        /// is never instantiated at all. One-shot: a full transform scan of the loaded
        /// scenes is far too heavy for the scan loop. A template of the prefab itself (no
        /// "(Clone)" suffix, parent not a zone) shows up as such: it proves the prefab
        /// exists without being placed in the world.
        /// </summary>
        private void Census(bool force)
        {
            if (censusDone && !force)
            {
                return;
            }

            censusDone = true;

            Transform[] all = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            nameCounts.Clear();
            List<string> chunkLines = new List<string>();
            int total = 0;

            foreach (Transform node in all)
            {
                if (!node.name.StartsWith(PilePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                total++;
                nameCounts.TryGetValue(node.name, out int count);
                nameCounts[node.name] = count + 1;

                if (IsTarget(node.name) && chunkLines.Count < 30)
                {
                    chunkLines.Add(
                        $"  {node.name} parent={node.parent?.name} at {node.position} "
                            + $"({(node.gameObject.activeInHierarchy ? "active" : "inactive")})"
                    );
                }
            }

            if (total == 0)
            {
                log.LogInfo("Census: no object named mudpile* in the loaded scenes");
                return;
            }

            List<string> parts = new List<string>(nameCounts.Count);
            foreach (KeyValuePair<string, int> pair in nameCounts)
            {
                parts.Add($"{pair.Key} x{pair.Value}");
            }

            log.LogInfo(
                $"Census: {total} object(s) named mudpile* in the loaded scenes - "
                    + $"{string.Join(", ", parts)}"
            );

            foreach (string line in chunkLines)
            {
                log.LogInfo(line);
            }

            if (chunkLines.Count == 0)
            {
                log.LogInfo(
                    $"Census: no '{string.Join("' or '", TargetNames)}' object anywhere "
                        + "in the loaded scenes"
                );
            }
        }

        /// <summary>
        /// DebugLog only, one-shot: lists every loaded object whose name carries "frac",
        /// whatever its components. This is how the "mudpile2_frac" the player strikes is
        /// identified for real - the name alone tells nothing about which component makes
        /// it minable, and the highlight has to hang off that component. A full transform
        /// scan of the loaded scenes, far too heavy for the scan loop: once per session.
        /// </summary>
        private void HuntFracs()
        {
            if (fracHunted)
            {
                return;
            }

            fracHunted = true;

            Transform[] all = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );

            int found = 0;
            foreach (Transform node in all)
            {
                if (node.name.IndexOf("frac", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                found++;
                log.LogInfo(
                    $"Frac hunt: {node.name} at {node.position} parent={node.parent?.name} "
                        + $"({(node.gameObject.activeInHierarchy ? "active" : "inactive")}) "
                        + $"{DescribeComponents(node)}"
                );
            }

            if (found == 0)
            {
                log.LogInfo("Frac hunt: no object with 'frac' in its name in the loaded scenes");
            }
        }

        private void DumpHierarchy(GameObject root)
        {
            log.LogInfo($"Hierarchy of {root.name} at {root.transform.position}:");

            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
            {
                int depth = 0;
                for (Transform up = node; up != null && up != root.transform; up = up.parent)
                {
                    depth++;
                }

                string state = node.gameObject.activeInHierarchy ? "+" : "-";
                log.LogInfo(
                    $"  {new string(' ', depth * 2)}[{state}] {node.name} "
                        + $"({DescribeComponents(node)})"
                );
            }
        }

        private static string DescribeComponents(Transform node)
        {
            Component[] components = node.GetComponents<Component>();
            List<string> names = new List<string>(components.Length);

            foreach (Component component in components)
            {
                if (names.Count >= 12)
                {
                    names.Add("...");
                    break;
                }

                names.Add(component == null ? "<missing script>" : component.GetType().Name);
            }

            return string.Join(", ", names);
        }

        private void LogScan(HashSet<GameObject> current, int rootsInRange)
        {
            nameCounts.Clear();
            foreach (GameObject chunk in current)
            {
                string name = chunk == null ? "<destroyed>" : chunk.name;
                nameCounts.TryGetValue(name, out int count);
                nameCounts[name] = count + 1;
            }

            List<string> parts = new List<string>(nameCounts.Count);
            foreach (KeyValuePair<string, int> pair in nameCounts)
            {
                parts.Add($"{pair.Key} x{pair.Value}");
            }

            Player player = Player.m_localPlayer;
            Vector3 origin = player != null ? player.transform.position : Vector3.zero;

            foreach (GameObject chunk in current)
            {
                if (chunk != null)
                {
                    log.LogInfo(
                        $"  {chunk.name} at {chunk.transform.position} "
                            + $"({Vector3.Distance(origin, chunk.transform.position):0.0} m, "
                            + $"{(chunk.activeInHierarchy ? "active" : "inactive")})"
                    );
                }
            }

            log.LogInfo(
                $"Scan: {current.Count} object(s) highlighted from {rootsInRange} pile(s) - "
                    + $"{string.Join(", ", parts)}"
            );
        }
    }
}
