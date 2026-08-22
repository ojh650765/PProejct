using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// A live effect handed out by the pool. Callers hold this rather than a raw
    /// GameObject so a persistent effect can be stopped explicitly and a one-shot
    /// can be forgotten about entirely.
    /// </summary>
    public sealed class VfxHandle
    {
        internal GameObject Root;
        internal ParticleSystem[] Systems;
        internal string Key;
        internal float ExpiresAt;
        internal bool Persistent;

        /// <summary>
        /// Which use of the instance this handle refers to; zero once the pool has taken it
        /// back.
        ///
        /// The GameObject outlives the play that borrowed it, so a caller holding a handle
        /// from a finished effect is holding a token for an instance somebody else is now
        /// using. Without the stamp, stopping a status aura that had already expired stopped
        /// whichever effect had since been handed that instance.
        /// </summary>
        internal int Generation;

        private bool Live => Generation != 0 && Root != null;

        public Transform Transform => Live ? Root.transform : null;
        public bool IsAlive => Live && Root.activeSelf;

        public void SetPosition(Vector3 worldPosition)
        {
            if (Live) Root.transform.position = worldPosition;
        }

        public void SetRotation(Quaternion rotation)
        {
            if (Live) Root.transform.rotation = rotation;
        }

        /// <summary>Uniformly scales the effect. Used to size a hit to the creature.</summary>
        public void SetScale(float scale)
        {
            if (Live) Root.transform.localScale = Vector3.one * Mathf.Max(scale, 0.01f);
        }
    }

    /// <summary>
    /// Pools every effect instance so a battle never calls Instantiate.
    ///
    /// The brief is explicit about this, and the reason is not allocation: it is
    /// that instantiating a ParticleSystem forces a synchronous shader and mesh
    /// setup the first time each one renders, which produces exactly the kind of
    /// single-frame hitch that shows up on the first attack of a battle and never
    /// again, so nobody ever reproduces it.
    ///
    /// Everything is warmed at registration time instead.
    /// </summary>
    public sealed class VfxPool
    {
        private sealed class Bucket
        {
            public VfxRecipe Recipe;
            public GameObject Prefab;         // null for procedural recipes
            public readonly Stack<VfxHandle> Idle = new Stack<VfxHandle>();
            public readonly List<VfxHandle> Active = new List<VfxHandle>();
            public int Created;
        }

        private const int HardCapPerKey = 24;

        private readonly Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>();
        private readonly List<VfxHandle> expiring = new List<VfxHandle>(32);
        private readonly Transform root;
        private int generation;

        public VfxPool(Transform poolRoot)
        {
            root = poolRoot;
        }

        /// <summary>
        /// Registers a key and optionally warms its instances. Safe to call repeatedly.
        ///
        /// prewarmNow defaults to zero because registering every key in the catalogue
        /// at boot would build several hundred ParticleSystems in a single frame. The
        /// presenter registers everything cheaply, then walks the list with Prewarm
        /// over the following frames.
        /// </summary>
        public void Register(string key, VfxRecipe recipe, GameObject prefab, int prewarmNow = 0)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (buckets.ContainsKey(key)) return;

            buckets[key] = new Bucket { Recipe = recipe, Prefab = prefab };
            if (prewarmNow > 0) Prewarm(key, prewarmNow);
        }

        /// <summary>
        /// Builds instances for a registered key up to its recipe's prewarm count.
        /// Returns how many it actually created, so a caller can budget the work
        /// across frames.
        /// </summary>
        public int Prewarm(string key, int maxToCreate = 1)
        {
            if (!buckets.TryGetValue(key, out var bucket)) return 0;

            int target = Mathf.Clamp(bucket.Recipe?.PoolPrewarm ?? 1, 0, 6);
            int created = 0;
            while (bucket.Created < target && created < maxToCreate)
            {
                var handle = CreateInstance(key, bucket);
                if (handle == null) break;
                bucket.Idle.Push(handle);
                created++;
            }
            return created;
        }

        /// <summary>True once the key holds at least its recipe's prewarm count.</summary>
        public bool IsWarm(string key) =>
            buckets.TryGetValue(key, out var bucket) &&
            bucket.Created >= Mathf.Clamp(bucket.Recipe?.PoolPrewarm ?? 1, 0, 6);

        public bool IsRegistered(string key) => !string.IsNullOrEmpty(key) && buckets.ContainsKey(key);

        /// <summary>
        /// Takes an instance, positions it and starts it. Returns null when the key
        /// is unknown or the hard cap is reached — a dropped effect is always
        /// preferable to a frame spike, and presentation is advisory by contract.
        /// </summary>
        public VfxHandle Play(string key, Vector3 position, Quaternion rotation,
                              float scale = 1f, Transform parent = null)
        {
            if (!buckets.TryGetValue(key, out var bucket)) return null;

            VfxHandle handle = null;
            while (bucket.Idle.Count > 0)
            {
                handle = bucket.Idle.Pop();
                if (handle.Root != null) break;
                // Destroyed while it sat in the pool. Hand the capacity back instead of
                // handing out a hole, for the same reason Recycle does.
                bucket.Created = Mathf.Max(0, bucket.Created - 1);
                handle = null;
            }
            if (handle == null) handle = CreateInstance(key, bucket);
            if (handle == null) return null;

            var t = handle.Root.transform;
            t.SetParent(parent != null ? parent : root, false);
            t.position = position;
            t.rotation = rotation;
            t.localScale = Vector3.one * Mathf.Max(scale, 0.01f);

            handle.Generation = ++generation;
            if (bucket.Recipe != null)
            {
                handle.Persistent = bucket.Recipe.TotalDuration <= 1e-4f;
                handle.ExpiresAt = handle.Persistent
                    ? float.MaxValue
                    : Time.time + bucket.Recipe.ResolveDuration();
            }
            else
            {
                // A prefab override has no recipe to ask, so the prefab is the authority.
                // A looping system is its author saying "this runs until someone stops it";
                // treating every override as a two-second one-shot force-recycled a looping
                // aura mid-loop, which is precisely the case an override exists to serve.
                handle.Persistent = AnyLooping(handle);
                handle.ExpiresAt = handle.Persistent
                    ? float.MaxValue
                    : Time.time + PrefabDuration(handle);
            }

            handle.Root.SetActive(true);
            for (int i = 0; i < handle.Systems.Length; i++)
            {
                var ps = handle.Systems[i];
                if (ps == null) continue;
                ps.Clear(false);
                ps.Play(false);
            }

            bucket.Active.Add(handle);
            return handle;
        }

        /// <summary>
        /// Stops a persistent effect. Emission ends immediately but existing
        /// particles are left to finish, so a status aura fades out rather than
        /// vanishing mid-frame.
        /// </summary>
        public void Stop(VfxHandle handle, bool immediate = false)
        {
            // A handle the pool has already reclaimed names an instance somebody else is
            // playing now, so stopping through it would stop their effect instead.
            if (handle == null || handle.Generation == 0 || handle.Root == null) return;

            for (int i = 0; i < handle.Systems.Length; i++)
            {
                var ps = handle.Systems[i];
                if (ps == null) continue;
                ps.Stop(false, immediate
                    ? ParticleSystemStopBehavior.StopEmittingAndClear
                    : ParticleSystemStopBehavior.StopEmitting);
            }

            handle.Persistent = false;
            handle.ExpiresAt = immediate ? Time.time : Time.time + LongestLifetime(handle);
        }

        private static float LongestLifetime(VfxHandle handle)
        {
            float longest = 0.3f;
            for (int i = 0; i < handle.Systems.Length; i++)
            {
                var ps = handle.Systems[i];
                if (ps == null) continue;
                var main = ps.main;
                longest = Mathf.Max(longest, main.startLifetime.constantMax);
            }
            return longest + 0.1f;
        }

        /// <summary>Called once a frame by the presenter. Returns expired instances.</summary>
        public void Tick()
        {
            float now = Time.time;
            foreach (var kv in buckets)
            {
                var active = kv.Value.Active;
                expiring.Clear();

                for (int i = 0; i < active.Count; i++)
                {
                    var handle = active[i];
                    if (handle.Root == null) { expiring.Add(handle); continue; }
                    if (!handle.Persistent && now >= handle.ExpiresAt) expiring.Add(handle);
                }

                for (int i = 0; i < expiring.Count; i++)
                {
                    var handle = expiring[i];
                    active.Remove(handle);
                    Recycle(kv.Value, handle);
                }
            }
        }

        private void Recycle(Bucket bucket, VfxHandle handle)
        {
            // Retired first, whichever way this goes: the caller may still be holding this
            // handle, and from here on it names an instance it no longer owns.
            handle.Generation = 0;

            if (handle.Root == null)
            {
                // Destroyed from outside — an effect parented to a creature that the battle
                // tore down while it was still running. The instance is gone, so the count
                // of instances has to lose it too; leaving it counted burned one of the
                // key's slots permanently, and twenty-four of those retired the key for the
                // rest of the session.
                bucket.Created = Mathf.Max(0, bucket.Created - 1);
                return;
            }

            for (int i = 0; i < handle.Systems.Length; i++)
            {
                var ps = handle.Systems[i];
                if (ps == null) continue;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            handle.Root.SetActive(false);
            handle.Root.transform.SetParent(root, false);

            // The instance goes back to the pool wrapped in a fresh handle, so the token the
            // caller kept cannot be mistaken for the next play of the same instance.
            bucket.Idle.Push(new VfxHandle
            {
                Root = handle.Root,
                Systems = handle.Systems,
                Key = handle.Key,
            });
        }

        /// <summary>Whether any of an instance's systems loops, i.e. it ends only on Stop.</summary>
        private static bool AnyLooping(VfxHandle handle)
        {
            for (int i = 0; i < handle.Systems.Length; i++)
            {
                var ps = handle.Systems[i];
                if (ps != null && ps.main.loop) return true;
            }
            return false;
        }

        /// <summary>
        /// How long a one-shot prefab needs before the pool may take it back: its own
        /// emission window plus the life of the last particle it emits.
        /// </summary>
        private static float PrefabDuration(VfxHandle handle)
        {
            float longest = 0f;
            for (int i = 0; i < handle.Systems.Length; i++)
            {
                var ps = handle.Systems[i];
                if (ps == null) continue;
                var main = ps.main;
                longest = Mathf.Max(longest, main.startDelay.constantMax
                                             + main.duration
                                             + main.startLifetime.constantMax);
            }
            // Nothing to measure means no particle systems at all, so fall back to the
            // two seconds this used for everything.
            return longest > 1e-3f ? longest + 0.15f : 2f;
        }

        private VfxHandle CreateInstance(string key, Bucket bucket)
        {
            if (bucket.Created >= HardCapPerKey)
            {
                Debug.LogWarning($"[VfxPool] Hard cap of {HardCapPerKey} reached for '{key}'. " +
                                 "Dropping the request rather than growing further.");
                return null;
            }

            GameObject go = bucket.Prefab != null
                ? Object.Instantiate(bucket.Prefab, root)
                : ProceduralVfxFactory.Build(bucket.Recipe, root);

            if (go == null) return null;

            go.name = key + "_" + bucket.Created;
            go.SetActive(false);
            bucket.Created++;

            return new VfxHandle
            {
                Root = go,
                Systems = go.GetComponentsInChildren<ParticleSystem>(true),
                Key = key,
            };
        }

        /// <summary>
        /// Destroys every pooled instance but keeps the registrations, so the same pool can be
        /// warmed again later without rebuilding the catalogue.
        ///
        /// <b>Why a second teardown next to Dispose.</b> Dispose ends the pool's life; this ends
        /// only its residency. The presenter that owns this pool is created once at boot and
        /// never destroyed, so Dispose runs when the game quits and at no other moment -- which
        /// meant the full warm pool sat in memory through the login screen, the main menu and
        /// the gacha, none of which can play an effect. Measured on the web build, that was
        /// 636 ParticleSystems and 167 lights resident before the player had typed a name, and
        /// the wasm heap it forced never came back down.
        ///
        /// Registrations survive because they are just recipes in a dictionary; rebuilding them
        /// would mean rebuilding the catalogue, and the instances are the part that costs.
        /// </summary>
        public void ReleaseInstances()
        {
            foreach (var kv in buckets)
            {
                var bucket = kv.Value;

                foreach (var handle in bucket.Idle) DestroyHandle(handle);
                bucket.Idle.Clear();

                for (int i = 0; i < bucket.Active.Count; i++)
                {
                    // Retire the stamp as well: a caller still holding one of these handles
                    // must not be able to stop an instance that no longer exists.
                    bucket.Active[i].Generation = 0;
                    DestroyHandle(bucket.Active[i]);
                }
                bucket.Active.Clear();

                bucket.Created = 0;
            }

            expiring.Clear();
        }

        /// <summary>Destroys every instance. Called on presenter teardown.</summary>
        public void Dispose()
        {
            foreach (var kv in buckets)
            {
                foreach (var handle in kv.Value.Idle) DestroyHandle(handle);
                for (int i = 0; i < kv.Value.Active.Count; i++) DestroyHandle(kv.Value.Active[i]);
            }
            buckets.Clear();
        }

        private static void DestroyHandle(VfxHandle handle)
        {
            if (handle?.Root == null) return;
            if (Application.isPlaying) Object.Destroy(handle.Root);
            else Object.DestroyImmediate(handle.Root);
        }
    }
}
