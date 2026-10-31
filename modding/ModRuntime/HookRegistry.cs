using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BepInEx.Logging;

namespace IOSPort.Mods
{
    // iOS can't rewrite code at runtime, so Harmony can't detour methods there. Instead the build
    // weaves a hook point into every game method a mod might patch:
    //
    //     if (HookRegistry.Active[id]) return HookRegistry.Invoke(id, this, args);
    //     ...original body...
    //
    // and a copy of the untouched body (Method__iosport_orig) to call as "the original".
    // 0Harmony's PatchFunctions.UpdateWrapper is rewritten to call Update() below, so mods keep using
    // the normal Harmony API (PatchAll, Patch, Unpatch) and their prefixes/postfixes run from here.
    public static class HookRegistry
    {
        const int MaxHooks = 65536;

        // Read by the woven stubs; Active[id] is true while the method has patches.
        public static readonly bool[] Active = new bool[MaxHooks];

        static readonly MethodBase[] originals = new MethodBase[MaxHooks];
        static readonly MethodInfo[] clones = new MethodInfo[MaxHooks];
        static readonly HookEntry[] entries = new HookEntry[MaxHooks];
        static readonly Dictionary<IntPtr, int> idsByHandle = new Dictionary<IntPtr, int>();
        static readonly HashSet<IntPtr> reportedMissing = new HashSet<IntPtr>();
        static readonly object locker = new object();

        internal static readonly ManualLogSource Log = Logger.CreateLogSource("iOS Mods");
        internal static readonly List<string> MissingHooks = new List<string>();
        internal static int RegisteredCount;

        // Called from the generated IOSPortGenerated.HookIds.Register() in each woven assembly.
        public static void Map(int id, RuntimeMethodHandle original, RuntimeTypeHandle originalType, RuntimeMethodHandle clone, RuntimeTypeHandle cloneType)
        {
            var originalMethod = MethodBase.GetMethodFromHandle(original, originalType);
            var cloneMethod = (MethodInfo)MethodBase.GetMethodFromHandle(clone, cloneType);
            lock (locker)
            {
                originals[id] = originalMethod;
                clones[id] = cloneMethod;
                idsByHandle[original.Value] = id;
                RegisteredCount++;
            }
        }

        // Replacement for HarmonyLib.PatchFunctions.UpdateWrapper(original, patchInfo).
        public static MethodInfo Update(MethodBase original, object patchInfo)
        {
            if (original == null)
                return null;
            int id;
            lock (locker)
            {
                if (!idsByHandle.TryGetValue(original.MethodHandle.Value, out id))
                {
                    if (reportedMissing.Add(original.MethodHandle.Value))
                    {
                        string name = Describe(original);
                        MissingHooks.Add(name);
                        Log.LogWarning("A mod patched " + name + ", which has no hook point in this build. " +
                                       "That patch won't run. Run ./build again after copying BepInEx/iosport/missing-hooks.txt from the phone into the BepInEx folder.");
                        ModLoader.WriteMissingHooks();
                    }
                    return null;
                }
            }

            var entry = HookEntry.Create(id, original, clones[id], patchInfo);
            lock (locker)
            {
                entries[id] = entry;
                Active[id] = entry != null;
            }
            return null;
        }

        // Replacement for HarmonyLib.MethodInvoker.GetHandler, which generates IL. delegateType is FastInvokeHandler.
        public static Delegate FastInvoker(MethodInfo method, Type delegateType)
        {
            if (method == null)
                return null;
            var invoker = new ReflectionInvoker(method);
            return Delegate.CreateDelegate(delegateType, invoker, typeof(ReflectionInvoker).GetMethod(nameof(ReflectionInvoker.Invoke)));
        }

        sealed class ReflectionInvoker
        {
            readonly MethodInfo method;
            public ReflectionInvoker(MethodInfo method) => this.method = method;
            public object Invoke(object target, object[] parameters) => method.Invoke(target, parameters);
        }

        // Called from a woven stub when Active[id] is set.
        public static object Invoke(int id, object instance, object[] args)
        {
            var entry = entries[id];
            if (entry == null)
                return clones[id].Invoke(instance, args);
            return entry.Run(instance, args);
        }

        internal static string Describe(MethodBase method)
        {
            var type = method.DeclaringType;
            string parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToArray());
            return (type == null ? "" : type.Assembly.GetName().Name + "|" + type.FullName + "|") + method.Name + "(" + parameters + ")";
        }
    }

    sealed class HookEntry
    {
        MethodBase original;
        MethodInfo clone;
        Type returnType;
        PatchCall[] prefixes, postfixes, finalizers;
        bool usesState;

        static readonly Type[] noTypes = new Type[0];

        public static HookEntry Create(int id, MethodBase original, MethodInfo clone, object patchInfo)
        {
            var prefixes = Sorted(original, patchInfo, "prefixes");
            var postfixes = Sorted(original, patchInfo, "postfixes");
            var finalizers = Sorted(original, patchInfo, "finalizers");
            foreach (var kind in new[] { "transpilers", "ilmanipulators" })
            {
                foreach (var method in Sorted(original, patchInfo, kind))
                    HookRegistry.Log.LogWarning("Skipping " + kind.TrimEnd('s') + " " + method.DeclaringType + "." + method.Name + " on " + HookRegistry.Describe(original) + ": IL rewriting isn't possible on iOS.");
            }
            if (prefixes.Count + postfixes.Count + finalizers.Count == 0)
                return null;

            var returnType = original is MethodInfo mi ? mi.ReturnType : typeof(void);
            var entry = new HookEntry
            {
                original = original,
                clone = clone,
                returnType = returnType,
                prefixes = prefixes.Select(m => new PatchCall(m, original, returnType)).ToArray(),
                postfixes = postfixes.Select(m => new PatchCall(m, original, returnType)).ToArray(),
                finalizers = finalizers.Select(m => new PatchCall(m, original, returnType)).ToArray(),
            };
            entry.usesState = entry.prefixes.Concat(entry.postfixes).Concat(entry.finalizers).Any(p => p.UsesState);
            return entry;
        }

        // Harmony sorts patches by priority/before/after; reuse its sorter when available.
        static List<MethodInfo> Sorted(MethodBase original, object patchInfo, string field)
        {
            var patches = patchInfo.GetType().GetField(field)?.GetValue(patchInfo) as Array;
            if (patches == null || patches.Length == 0)
                return new List<MethodInfo>();
            try
            {
                var functions = patchInfo.GetType().Assembly.GetType("HarmonyLib.PatchFunctions");
                var sort = functions.GetMethod("GetSortedPatchMethods", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                return (List<MethodInfo>)sort.Invoke(null, new object[] { original, patches, false });
            }
            catch (Exception e)
            {
                HookRegistry.Log.LogDebug("Harmony patch sorter unavailable, sorting by priority: " + e.Message);
                return patches.Cast<object>()
                    .Select((p, index) => new { p, index })
                    .OrderByDescending(x => (int)x.p.GetType().GetField("priority").GetValue(x.p))
                    .ThenBy(x => x.index)
                    .Select(x => (MethodInfo)x.p.GetType().GetProperty("PatchMethod").GetValue(x.p, null))
                    .ToList();
            }
        }

        public object Run(object instance, object[] args)
        {
            var call = new CallState
            {
                Instance = instance,
                Args = args,
                Original = original,
                Result = returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType),
                RunOriginal = true,
                State = usesState ? new Dictionary<Type, object>() : null,
            };

            foreach (var prefix in prefixes)
            {
                object ret = prefix.Invoke(call);
                if (ret is bool keepGoing && !keepGoing)
                    call.RunOriginal = false;
            }

            Exception exception = null;
            if (call.RunOriginal)
            {
                try
                {
                    call.Result = clone.Invoke(instance, args);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    exception = e.InnerException;
                }
            }

            if (exception == null)
            {
                foreach (var postfix in postfixes)
                {
                    object ret = postfix.Invoke(call);
                    if (postfix.PassesThroughResult)
                        call.Result = ret;
                }
            }

            if (finalizers.Length > 0)
            {
                call.Exception = exception;
                foreach (var finalizer in finalizers)
                {
                    object ret = finalizer.Invoke(call);
                    if (finalizer.ReturnsException)
                        call.Exception = (Exception)ret;
                }
                exception = call.Exception;
            }

            if (exception != null)
                ExceptionDispatchInfo.Capture(exception).Throw();
            return call.Result;
        }
    }

    sealed class CallState
    {
        public object Instance;
        public object[] Args;
        public MethodBase Original;
        public object Result;
        public bool RunOriginal;
        public Exception Exception;
        public Dictionary<Type, object> State;
    }

    // One prefix/postfix/finalizer, with its parameters resolved once using Harmony's injection names.
    sealed class PatchCall
    {
        enum Kind { Instance, Result, State, Args, OriginalMethod, RunOriginal, Exception, Field, Argument, Unknown }

        struct Binding
        {
            public Kind Kind;
            public int Index;
            public FieldInfo Field;
            public bool ByRef;
        }

        readonly MethodInfo method;
        readonly Binding[] bindings;
        public readonly bool UsesState;
        public readonly bool PassesThroughResult;
        public readonly bool ReturnsException;

        public PatchCall(MethodInfo method, MethodBase original, Type returnType)
        {
            this.method = method;
            var originalParams = original.GetParameters();
            var parameters = method.GetParameters();
            bindings = new Binding[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var b = new Binding { ByRef = p.ParameterType.IsByRef, Index = -1 };
                string name = p.Name;
                int? argIndex = HarmonyArgumentIndex(p, originalParams);
                if (argIndex.HasValue) { b.Kind = Kind.Argument; b.Index = argIndex.Value; }
                else if (name == "__instance") b.Kind = Kind.Instance;
                else if (name == "__result") b.Kind = Kind.Result;
                else if (name == "__state") { b.Kind = Kind.State; UsesState = true; }
                else if (name == "__args") b.Kind = Kind.Args;
                else if (name == "__originalMethod") b.Kind = Kind.OriginalMethod;
                else if (name == "__runOriginal") b.Kind = Kind.RunOriginal;
                else if (name == "__exception") b.Kind = Kind.Exception;
                else if (name.StartsWith("___"))
                {
                    b.Kind = Kind.Field;
                    b.Field = FindField(original.DeclaringType, name.Substring(3));
                    if (b.Field == null)
                        HookRegistry.Log.LogWarning(method.DeclaringType + "." + method.Name + ": no field '" + name.Substring(3) + "' on " + original.DeclaringType);
                }
                else if (name.StartsWith("__") && int.TryParse(name.Substring(2), out int n)) { b.Kind = Kind.Argument; b.Index = n; }
                else
                {
                    b.Index = Array.FindIndex(originalParams, op => op.Name == name);
                    b.Kind = b.Index >= 0 ? Kind.Argument : Kind.Unknown;
                    if (b.Index < 0)
                        HookRegistry.Log.LogWarning(method.DeclaringType + "." + method.Name + ": can't bind parameter '" + name + "'");
                }
                bindings[i] = b;
            }

            PassesThroughResult = returnType != typeof(void) && method.ReturnType == returnType
                && parameters.Length > 0 && parameters[0].ParameterType == returnType;
            ReturnsException = method.ReturnType == typeof(Exception);
        }

        public object Invoke(CallState call)
        {
            var values = new object[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                switch (b.Kind)
                {
                    case Kind.Instance: values[i] = call.Instance; break;
                    case Kind.Result: values[i] = call.Result; break;
                    case Kind.State:
                        call.State.TryGetValue(method.DeclaringType, out values[i]);
                        break;
                    case Kind.Args: values[i] = call.Args; break;
                    case Kind.OriginalMethod: values[i] = call.Original; break;
                    case Kind.RunOriginal: values[i] = call.RunOriginal; break;
                    case Kind.Exception: values[i] = call.Exception; break;
                    case Kind.Field: values[i] = b.Field?.GetValue(b.Field.IsStatic ? null : call.Instance); break;
                    case Kind.Argument: values[i] = b.Index < call.Args.Length ? call.Args[b.Index] : null; break;
                }
                if (values[i] == null && b.Kind != Kind.Unknown)
                {
                    var type = method.GetParameters()[i].ParameterType;
                    if (type.IsByRef) type = type.GetElementType();
                    if (type.IsValueType && b.Kind != Kind.State) values[i] = Activator.CreateInstance(type);
                }
            }

            object ret;
            try
            {
                ret = method.Invoke(null, values);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                HookRegistry.Log.LogError("Patch " + method.DeclaringType + "." + method.Name + " threw: " + e.InnerException);
                return null;
            }

            for (int i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                if (b.Kind == Kind.State)
                {
                    // __state is shared between the prefix and postfix of the same patch class.
                    call.State[method.DeclaringType] = values[i];
                    continue;
                }
                if (!b.ByRef)
                    continue;
                switch (b.Kind)
                {
                    case Kind.Result: call.Result = values[i]; break;
                    case Kind.RunOriginal: call.RunOriginal = (bool)values[i]; break;
                    case Kind.Exception: call.Exception = (Exception)values[i]; break;
                    case Kind.Field:
                        if (b.Field != null && !b.Field.IsInitOnly) b.Field.SetValue(b.Field.IsStatic ? null : call.Instance, values[i]);
                        break;
                    case Kind.Argument:
                        if (b.Index < call.Args.Length) call.Args[b.Index] = values[i];
                        break;
                }
            }
            return ret;
        }

        static int? HarmonyArgumentIndex(ParameterInfo p, ParameterInfo[] originalParams)
        {
            foreach (var attr in p.GetCustomAttributes(false))
            {
                var type = attr.GetType();
                if (type.FullName != "HarmonyLib.HarmonyArgument")
                    continue;
                var originalName = type.GetProperty("OriginalName")?.GetValue(attr, null) as string;
                var index = type.GetProperty("Index")?.GetValue(attr, null) as int?;
                if (!string.IsNullOrEmpty(originalName))
                {
                    int found = Array.FindIndex(originalParams, op => op.Name == originalName);
                    if (found >= 0) return found;
                }
                if (index.HasValue && index.Value >= 0)
                    return index.Value;
            }
            return null;
        }

        static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null)
                    return f;
            }
            return null;
        }
    }
}
