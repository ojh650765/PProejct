using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Duck-typed adapter for services that are not declared in <c>Core</c>.
    ///
    /// Why this exists: <see cref="ServiceHub"/> keys on the exact interface type, and the
    /// cinematics and battle-staging contracts were never frozen into <c>Core</c>. If this
    /// assembly declared them, the owning worker would have to reference <c>PokeLab.Overworld</c>
    /// to register against them — coupling the contract forbids. So we resolve in three steps:
    ///
    /// 1. <see cref="ServiceHub.TryGet{T}"/> against the local interface, for the day the
    ///    integrator promotes the contract into <c>Core</c> and the coupling becomes legal.
    /// 2. A serialized <see cref="UnityEngine.Object"/> the integrator drags in, invoked by
    ///    method name through reflection.
    /// 3. Nothing — the caller degrades. Every call site here must survive a null bridge.
    ///
    /// Reflection cost is paid once per method and cached; these are transition-frequency
    /// calls, not per-frame ones.
    /// </summary>
    public sealed class ServiceBridge
    {
        private readonly object _target;
        private readonly Dictionary<string, MethodInfo> _cache = new Dictionary<string, MethodInfo>();

        public ServiceBridge(object target) => _target = target;

        public bool IsValid => _target != null && !(_target is UnityEngine.Object uo && uo == null);

        /// <summary>The wrapped instance, so a caller that knows the real type can cast.</summary>
        public object Target => _target;

        /// <summary>
        /// Invokes <paramref name="method"/> on the target if a compatible overload exists.
        /// Returns false when the member is absent so the caller can take its fallback path.
        /// </summary>
        public bool TryInvoke(string method, out object result, params object[] args)
        {
            result = null;
            if (!IsValid) return false;

            var info = Resolve(method, args);
            if (info == null) return false;

            try
            {
                result = info.Invoke(_target, args);
                return true;
            }
            catch (TargetInvocationException ex)
            {
                // Surface the real exception: a swallowed transition failure looks like a hang.
                Debug.LogError($"[ServiceBridge] {_target.GetType().Name}.{method} threw: {ex.InnerException}");
                return false;
            }
        }

        public bool TryInvoke(string method, params object[] args) => TryInvoke(method, out _, args);

        public bool Has(string method, params object[] args) => IsValid && Resolve(method, args) != null;

        private MethodInfo Resolve(string method, object[] args)
        {
            var argCount = args?.Length ?? 0;
            var key = method + "/" + argCount;
            if (_cache.TryGetValue(key, out var cached)) return cached;

            MethodInfo found = null;
            var candidates = _target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate.Name != method) continue;
                var parameters = candidate.GetParameters();
                if (parameters.Length != argCount) continue;
                if (!ParametersMatch(parameters, args)) continue;
                found = candidate;
                break;
            }

            _cache[key] = found;
            return found;
        }

        private static bool ParametersMatch(ParameterInfo[] parameters, object[] args)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                var arg = args[i];
                var expected = parameters[i].ParameterType;
                if (arg == null)
                {
                    // A null is only assignable to a reference or nullable parameter.
                    if (expected.IsValueType && Nullable.GetUnderlyingType(expected) == null) return false;
                    continue;
                }
                if (!expected.IsInstanceOfType(arg)) return false;
            }
            return true;
        }

        /// <summary>
        /// Resolves a bridge for a service, preferring a registered <typeparamref name="T"/> in
        /// <see cref="ServiceHub"/> and falling back to an inspector-assigned object.
        /// </summary>
        public static ServiceBridge Resolve<T>(UnityEngine.Object inspectorFallback) where T : class
        {
            if (ServiceHub.TryGet<T>(out var service)) return new ServiceBridge(service);
            if (inspectorFallback != null) return new ServiceBridge(inspectorFallback);
            return new ServiceBridge(null);
        }
    }
}
