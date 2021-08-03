using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.Persistent;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace Multiplayer.Client
{
    // For a derived type, reduces to syncing the first field found
    // Used to attach lightweight data to objects during syncing
    // For example: public record TestWrapper(Pawn pawn); syncs the Pawn inside
    public abstract record SyncWrapper;

    public static class SyncSerialization
    {
        public static Type[] storageParents;
        public static Type[] plantToGrowSettables;

        private static Type[] AllImplementationsOrdered(Type type)
        {
            return GenTypes.AllTypes
                .Where(t => t != type && type.IsAssignableFrom(t))
                .OrderBy(t => t.IsInterface)
                .ThenBy(t => t.Name)
                .ToArray();
        }

        private static Type[] AllSubclassesNonAbstractOrdered(Type type) {
            return type
                .AllSubclassesNonAbstract()
                .OrderBy(t => t.Name)
                .ToArray();
        }

        public static Type[] thingCompTypes;
        public static Type[] abilityCompTypes;
        public static Type[] designatorTypes;
        public static Type[] worldObjectCompTypes;

        public static Type[] gameCompTypes;
        public static Type[] worldCompTypes;
        public static Type[] mapCompTypes;

        public static void CollectTypes()
        {
            storageParents = AllImplementationsOrdered(typeof(IStoreSettingsParent));
            plantToGrowSettables = AllImplementationsOrdered(typeof(IPlantToGrowSettable));

            thingCompTypes = AllSubclassesNonAbstractOrdered(typeof(ThingComp));
            abilityCompTypes = AllSubclassesNonAbstractOrdered(typeof(AbilityComp));
            designatorTypes = AllSubclassesNonAbstractOrdered(typeof(Designator));
            worldObjectCompTypes = AllSubclassesNonAbstractOrdered(typeof(WorldObjectComp));

            gameCompTypes = AllSubclassesNonAbstractOrdered(typeof(GameComponent));
            worldCompTypes = AllSubclassesNonAbstractOrdered(typeof(WorldComponent));
            mapCompTypes = AllSubclassesNonAbstractOrdered(typeof(MapComponent));
        }

        internal static Type[] supportedThingHolders = new[]
        {
            typeof(Map),
            typeof(Thing),
            typeof(ThingComp),
            typeof(WorldObject),
            typeof(WorldObjectComp)
        };

        private static MethodInfo ReadExposable = AccessTools.Method(typeof(ScribeUtil), nameof(ScribeUtil.ReadExposable));

        internal enum ListType : byte
        {
            Normal, MapAllThings, MapAllDesignations
        }

        internal enum ISelectableImpl : byte
        {
            None, Thing, Zone, WorldObject
        }

        internal enum VerbOwnerType : byte
        {
            None, Pawn, Ability, CompEquippable, CompReloadable
        }

        private static MethodInfo GetDefByIdMethod = AccessTools.Method(typeof(SyncSerialization), nameof(SyncSerialization.GetDefById));

        public static T GetDefById<T>(ushort id) where T : Def => DefDatabase<T>.GetByShortHash(id);

        public static T ReadSync<T>(ByteReader data)
        {
            return (T)ReadSyncObject(data, typeof(T));
        }

        public static object ReadSyncObject(ByteReader data, SyncType syncType)
        {
            var log = (data as LoggingByteReader)?.log;
            Type type = syncType.type;

            log?.Enter(type.FullName);

            try
            {
                object val = ReadSyncObjectInternal(data, syncType);
                log?.AppendToCurrentName($": {val}");
                return val;
            }
            finally
            {
                log?.Exit();
            }
        }

        private static object ReadSyncObjectInternal(ByteReader data, SyncType syncType)
        {
            MpContext context = data.MpContext();
            Map map = context.map;
            Type type = syncType.type;

            try
            {
                if (typeof(object) == type)
                {
                    return null;
                }

                if (type.IsByRef)
                {
                    return null;
                }

                if (SyncDictionary.syncWorkersEarly.TryGetValue(type, out SyncWorkerEntry syncWorkerEntryEarly)) {
                    object res = null;

                    if (syncWorkerEntryEarly.shouldConstruct || type.IsValueType)
                        res = Activator.CreateInstance(type);

                    syncWorkerEntryEarly.Invoke(new ReadingSyncWorker(data), ref res);

                    return res;
                }

                if (syncType.expose)
                {
                    if (!typeof(IExposable).IsAssignableFrom(type))
                        throw new SerializationException($"Type {type} can't be exposed because it isn't IExposable");

                    byte[] exposableData = data.ReadPrefixedBytes();
                    return ReadExposable.MakeGenericMethod(type).Invoke(null, new[] { exposableData, null });
                }

                if (typeof(ISynchronizable).IsAssignableFrom(type))
                {
                    var obj = Activator.CreateInstance(type);

                    ((ISynchronizable) obj).Sync(new ReadingSyncWorker(data));
                    return obj;
                }

                if (type.IsEnum) {
                    Type underlyingType = Enum.GetUnderlyingType(type);

                    return Enum.ToObject(type, ReadSyncObject(data, underlyingType));
                }

                if (type.IsArray && type.GetArrayRank() == 1)
                {
                    Type elementType = type.GetElementType();
                    ushort length = data.ReadUShort();
                    Array arr = Array.CreateInstance(elementType, length);
                    for (int i = 0; i < length; i++)
                        arr.SetValue(ReadSyncObject(data, elementType), i);
                    return arr;
                }

                if (type.IsGenericType)
                {
                    var genericTypeDefinition = type.GetGenericTypeDefinition();

                    if (genericTypeDefinition == typeof(List<>))
                    {
                        ListType specialList = ReadSync<ListType>(data);
                        if (specialList == ListType.MapAllThings)
                            return map.listerThings.AllThings;

                        if (specialList == ListType.MapAllDesignations)
                            return map.designationManager.allDesignations;

                        Type listType = type.GetGenericArguments()[0];
                        ushort size = data.ReadUShort();
                        IList list = (IList)Activator.CreateInstance(type, size);
                        for (int j = 0; j < size; j++)
                            list.Add(ReadSyncObject(data, listType));

                        return list;
                    }

                    if (genericTypeDefinition == typeof(IEnumerable<>))
                    {
                        Type element = type.GetGenericArguments()[0];
                        return ReadSyncObject(data, typeof(List<>).MakeGenericType(element));
                    }

                    if (genericTypeDefinition == typeof(Nullable<>))
                    {
                        bool isNull = data.ReadBool();
                        if (isNull) return null;

                        return Activator.CreateInstance(type, ReadSyncObject(data, Nullable.GetUnderlyingType(type)));
                    }

                    if (genericTypeDefinition == typeof(Dictionary<,>))
                    {
                        Type[] arguments = type.GetGenericArguments();

                        Array keys = (Array)ReadSyncObject(data, arguments[0].MakeArrayType());
                        Array values = (Array)ReadSyncObject(data, arguments[1].MakeArrayType());

                        IDictionary dictionary = (IDictionary)Activator.CreateInstance(type);
                        for (int i = 0; i < keys.Length; i++)
                            dictionary.Add(keys.GetValue(i), values.GetValue(i));
                        return dictionary;
                    }
                }

                if (typeof(SyncWrapper).IsAssignableFrom(type))
                {
                    var field = AccessTools.GetDeclaredFields(type)[0];
                    var obj = ReadSyncObject(data, field.FieldType);
                    return Activator.CreateInstance(type, obj);
                }

                // Def is a special case until the workers can read their own type
                if (typeof(Def).IsAssignableFrom(type))
                {
                    ushort shortHash = data.ReadUShort();
                    if (shortHash == 0)
                        return null;

                    Def def = (Def)GetDefByIdMethod.MakeGenericMethod(type).Invoke(null, new object[] { shortHash });
                    if (def == null)
                        throw new Exception($"Couldn't find {type} with short hash {shortHash}");

                    return def;
                }

                // Designators can't be handled by SyncWorkers due to the type change
                if (typeof(Designator).IsAssignableFrom(type))
                {
                    ushort desId = ReadSync<ushort>(data);
                    type = designatorTypes[desId]; // Replaces the type!
                }

                // Where the magic happens
                if (SyncDictionary.syncWorkers.TryGetValue(type, out var syncWorkerEntry)) 
                {
                    object res = null;

                    if (syncWorkerEntry.shouldConstruct || type.IsValueType)
                        res = Activator.CreateInstance(type);

                    syncWorkerEntry.Invoke(new ReadingSyncWorker(data), ref res);

                    return res;
                }

                throw new SerializationException("No reader for type " + type);
            }
            catch
            {
                MpLog.Error($"Error reading type: {type}");
                throw;
            }
        }

        public static object[] ReadSyncObjects(ByteReader data, IEnumerable<SyncType> spec)
        {
            return spec.Select(type => ReadSyncObject(data, type)).ToArray();
        }

        public static object[] ReadSyncObjects(ByteReader data, IEnumerable<Type> spec)
        {
            return spec.Select(type => ReadSyncObject(data, type)).ToArray();
        }

        public static void WriteSync<T>(ByteWriter data, T obj)
        {
            WriteSyncObject(data, obj, typeof(T));
        }

        public static void WriteSyncObject(ByteWriter data, object obj, SyncType syncType)
        {
            MpContext context = data.MpContext();
            Type type = syncType.type;

            var log = (data as LoggingByteWriter)?.log;
            log?.Enter($"{type.FullName}: {obj ?? "null"}");

            if (obj != null && !type.IsAssignableFrom(obj.GetType()))
                throw new SerializationException($"Serializing with type {type} but got object of type {obj.GetType()}");

            try
            {
                if (typeof(object) == type)
                {
                    return;
                }

                if (type.IsByRef)
                {
                    return;
                }

                if (SyncDictionary.syncWorkersEarly.TryGetValue(type, out var syncWorkerEntryEarly)) {
                    syncWorkerEntryEarly.Invoke(new WritingSyncWorker(data), ref obj);

                    return;
                }

                if (syncType.expose)
                {
                    if (!typeof(IExposable).IsAssignableFrom(type))
                        throw new SerializationException($"Type {type} can't be exposed because it isn't IExposable");

                    IExposable exposable = obj as IExposable;
                    byte[] xmlData = ScribeUtil.WriteExposable(exposable);
                    LogXML(log, xmlData);
                    data.WritePrefixedBytes(xmlData);

                    return;
                }

                if (typeof(ISynchronizable).IsAssignableFrom(type))
                {
                    ((ISynchronizable) obj).Sync(new WritingSyncWorker(data));

                    return;
                }

                if (type.IsEnum)
                {
                    Type enumType = Enum.GetUnderlyingType(type);

                    WriteSyncObject(data, Convert.ChangeType(obj, enumType), enumType);

                    return;
                }

                if (type.IsArray && type.GetArrayRank() == 1)
                {
                    Type elementType = type.GetElementType();
                    Array arr = (Array)obj;

                    if (arr.Length > ushort.MaxValue)
                        throw new Exception($"Tried to serialize a {elementType}[] with too many ({arr.Length}) items.");

                    data.WriteUShort((ushort)arr.Length);
                    foreach (object e in arr)
                        WriteSyncObject(data, e, elementType);

                    return;
                }

                if (type.IsGenericType) {
                    Type genericTypeDefinition = type.GetGenericTypeDefinition();

                    if (genericTypeDefinition == typeof(List<>))
                    {
                        ListType specialList = ListType.Normal;
                        Type listType = type.GetGenericArguments()[0];

                        if (listType == typeof(Thing) && obj == Find.CurrentMap.listerThings.AllThings)
                        {
                            context.map = Find.CurrentMap;
                            specialList = ListType.MapAllThings;
                        }
                        else if (listType == typeof(Designation) && obj == Find.CurrentMap.designationManager.allDesignations)
                        {
                            context.map = Find.CurrentMap;
                            specialList = ListType.MapAllDesignations;
                        }

                        WriteSync(data, specialList);

                        if (specialList == ListType.Normal)
                        {
                            IList list = (IList)obj;

                            if (list.Count > ushort.MaxValue)
                                throw new Exception($"Tried to serialize a {type} with too many ({list.Count}) items.");

                            data.WriteUShort((ushort)list.Count);
                            foreach (object e in list)
                                WriteSyncObject(data, e, listType);
                        }

                        return;
                    }

                    if (genericTypeDefinition == typeof(IEnumerable<>))
                    {
                        IEnumerable e = (IEnumerable)obj;
                        Type elementType = type.GetGenericArguments()[0];
                        var listType = typeof(List<>).MakeGenericType(elementType);
                        IList list = (IList)Activator.CreateInstance(listType);

                        foreach (var o in e)
                        {
                            if (list.Count > ushort.MaxValue)
                                throw new Exception($"Tried to serialize a {type} with too many ({list.Count}) items.");
                            list.Add(o);
                        }

                        WriteSyncObject(data, list, listType);

                        return;
                    }

                    if (genericTypeDefinition == typeof(Nullable<>)) 
                    {
                        bool isNull = obj == null;
                        data.WriteBool(isNull);
                        if (isNull) return;

                        WriteSyncObject(data, obj, Nullable.GetUnderlyingType(type));

                        return;
                    }

                    if (genericTypeDefinition == typeof(Dictionary<,>))
                    {
                        Type[] arguments = type.GetGenericArguments();

                        IDictionary dictionary = (IDictionary)obj;

                        Array keyArray = Array.CreateInstance(arguments[0], dictionary.Count);
                        dictionary.Keys.CopyTo(keyArray, 0);

                        Array valueArray = Array.CreateInstance(arguments[1], dictionary.Count);
                        dictionary.Values.CopyTo(valueArray, 0);

                        WriteSyncObject(data, keyArray, keyArray.GetType());
                        WriteSyncObject(data, valueArray, valueArray.GetType());

                        return;
                    }
                }

                if (typeof(SyncWrapper).IsAssignableFrom(type))
                {
                    var field = AccessTools.GetDeclaredFields(type)[0];
                    WriteSyncObject(data, field.GetValue(obj), field.FieldType);
                    return;
                }

                // Special case
                if (typeof(Def).IsAssignableFrom(type))
                {
                    Def def = obj as Def;
                    data.WriteUShort(def != null ? def.shortHash : (ushort)0);

                    return;
                }

                // Special case for Designators to change the type
                if (typeof(Designator).IsAssignableFrom(type))
                {
                    data.WriteUShort((ushort) Array.IndexOf(designatorTypes, obj.GetType()));
                }

                // Where the magic happens
                if (SyncDictionary.syncWorkers.TryGetValue(type, out var syncWorkerEntry))
                {
                    syncWorkerEntry.Invoke(new WritingSyncWorker(data), ref obj);

                    return;
                }

                log?.Node("No writer for " + type);
                throw new SerializationException("No writer for type " + type);
            }
            catch
            {
                MpLog.Error($"Error writing type: {type}, obj: {obj}, obj type: {obj?.GetType()}");
                throw;
            }
            finally
            {
                log?.Exit();
            }
        }

        internal static T ReadWithImpl<T>(ByteReader data, IList<Type> impls) where T : class
        {
            ushort impl = data.ReadUShort();
            if (impl == ushort.MaxValue) return null;
            return (T)ReadSyncObject(data, impls[impl]);
        }

        internal static void WriteWithImpl<T>(ByteWriter data, object obj, IList<Type> impls) where T : class
        {
            if (obj == null)
            {
                data.WriteUShort(ushort.MaxValue);
                return;
            }

            GetImpl(obj, impls, out Type implType, out int impl);

            if (implType == null)
                throw new SerializationException($"Unknown {typeof(T)} implementation type {obj.GetType()}");

            data.WriteUShort((ushort)impl);
            WriteSyncObject(data, obj, implType);
        }

        internal static void GetImpl(object obj, IList<Type> impls, out Type type, out int index)
        {
            type = null;
            index = -1;

            if (obj == null) return;

            for (int i = 0; i < impls.Count; i++)
            {
                if (impls[i].IsAssignableFrom(obj.GetType()))
                {
                    type = impls[i];
                    index = i;
                    break;
                }
            }
        }

        internal static T GetAnyParent<T>(Thing thing) where T : class
        {
            T t = thing as T;
            if (t != null)
                return t;

            for (var parentHolder = thing.ParentHolder; parentHolder != null; parentHolder = parentHolder.ParentHolder)
                if (parentHolder is T t2)
                    return t2;

            return (T)((object)null);
        }

        internal static string ThingHolderString(Thing thing)
        {
            StringBuilder builder = new StringBuilder(thing.ToString());

            for (var parentHolder = thing.ParentHolder; parentHolder != null; parentHolder = parentHolder.ParentHolder)
            {
                builder.Insert(0, "=>");
                builder.Insert(0, parentHolder.ToString());
            }

            return builder.ToString();
        }

        internal static IEnumerable<ISessionWithTransferables> GetSessions(Map map)
        {
            foreach (var s in Multiplayer.WorldComp.trading)
                yield return s;

            if (Multiplayer.WorldComp.splitSession != null)
                yield return Multiplayer.WorldComp.splitSession;

            if (map == null) yield break;
            var mapComp = map.MpComp();

            if (mapComp.caravanForming != null)
                yield return mapComp.caravanForming;

            if (mapComp.transporterLoading != null)
                yield return mapComp.transporterLoading;
        }

        private static void LogXML(SyncLogger log, byte[] xmlData)
        {
            if (log == null) return;

            var reader = XmlReader.Create(new MemoryStream(xmlData));

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string name = reader.Name;
                    if (reader.GetAttribute("IsNull") == "True")
                        name += " (IsNull)";

                    if (reader.IsEmptyElement)
                        log.Node(name);
                    else
                        log.Enter(name);
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    log.Exit();
                }
                else if (reader.NodeType == XmlNodeType.Text)
                {
                    log.AppendToCurrentName($": {reader.Value}");
                }
            }
        }
    }

    public class SerializationException : Exception
    {
        public SerializationException(string msg) : base(msg)
        {
        }
    }

}
