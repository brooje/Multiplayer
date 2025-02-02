using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Client.AsyncTime;
using Multiplayer.Common;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Multiplayer.Client.Util;
using UnityEngine;
using Verse;
using static Verse.Widgets;

namespace Multiplayer.Client.Persistent
{
    public class RitualSession : ISession
    {
        public Map map;
        public RitualData data;

        public Map Map => map;
        public int SessionId { get; private set; }

        public RitualSession(Map map)
        {
            this.map = map;
        }

        public RitualSession(Map map, RitualData data)
        {
            SessionId = Multiplayer.GlobalIdBlock.NextId();

            this.map = map;
            this.data = data;
            this.data.assignments.session = this;
        }

        [SyncMethod]
        public void Remove()
        {
            map.MpComp().ritualSession = null;
        }

        [SyncMethod]
        public void Start()
        {
            if (data.action != null && data.action(data.assignments))
                Remove();
        }

        public void OpenWindow()
        {
            var dialog = new BeginRitualProxy(
                null,
                data.ritualLabel,
                data.ritual,
                data.target,
                map,
                data.action,
                data.organizer,
                data.obligation,
                null,
                data.confirmText,
                null,
                null,
                null,
                data.outcome,
                data.extraInfos,
                null
            )
            {
                assignments = data.assignments
            };

            Find.WindowStack.Add(dialog);
        }

        public void Write(ByteWriter writer)
        {
            writer.WriteInt32(SessionId);
            writer.MpContext().map = map;

            SyncSerialization.WriteSync(writer, data);
        }

        public void Read(ByteReader reader)
        {
            SessionId = reader.ReadInt32();
            reader.MpContext().map = map;

            data = SyncSerialization.ReadSync<RitualData>(reader);
            data.assignments.session = this;
        }
    }

    public class MpRitualAssignments : RitualRoleAssignments
    {
        public RitualSession session;
    }

    public class BeginRitualProxy : Dialog_BeginRitual, ISwitchToMap
    {
        public static BeginRitualProxy drawing;

        public RitualSession Session => map.MpComp().ritualSession;

        public BeginRitualProxy(string header, string ritualLabel, Precept_Ritual ritual, TargetInfo target, Map map, ActionCallback action, Pawn organizer, RitualObligation obligation, Func<Pawn, bool, bool, bool> filter = null, string confirmText = null, List<Pawn> requiredPawns = null, Dictionary<string, Pawn> forcedForRole = null, string ritualName = null, RitualOutcomeEffectDef outcome = null, List<string> extraInfoText = null, Pawn selectedPawn = null) : base(header, ritualLabel, ritual, target, map, action, organizer, obligation, filter, confirmText, requiredPawns, forcedForRole, ritualName, outcome, extraInfoText, selectedPawn)
        {
            soundClose = SoundDefOf.TabClose;
        }

        public override void DoWindowContents(Rect inRect)
        {
            drawing = this;

            try
            {
                var session = Session;

                if (session == null)
                {
                    soundClose = SoundDefOf.Click;
                    Close();
                }

                // Make space for the "Switch to map" button
                inRect.yMin += 20f;

                base.DoWindowContents(inRect);
            }
            finally
            {
                drawing = null;
            }
        }
    }

    [HarmonyPatch(typeof(Widgets), nameof(Widgets.ButtonTextWorker))]
    static class MakeCancelRitualButtonRed
    {
        static void Prefix(string label, ref bool __state)
        {
            if (BeginRitualProxy.drawing == null) return;
            if (label != "CancelButton".Translate()) return;

            GUI.color = new Color(1f, 0.3f, 0.35f);
            __state = true;
        }

        static void Postfix(bool __state, ref DraggableResult __result)
        {
            if (!__state) return;

            GUI.color = Color.white;
            if (__result.AnyPressed())
            {
                BeginRitualProxy.drawing.Session?.Remove();
                __result = DraggableResult.Idle;
            }
        }
    }

    [HarmonyPatch]
    static class HandleStartRitual
    {
        static MethodBase TargetMethod()
        {
            return MpMethodUtil.GetLocalFunc(
                typeof(Dialog_BeginRitual),
                nameof(Dialog_BeginRitual.DoWindowContents),
                localFunc: "Start"
            );
        }

        static bool Prefix(Dialog_BeginRitual __instance)
        {
            if (__instance is BeginRitualProxy proxy)
            {
                proxy.Session.Start();
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    static class CancelDialogBeginRitual
    {
        static bool Prefix(Window window)
        {
            if (Multiplayer.Client != null
                && window.GetType() == typeof(Dialog_BeginRitual) // Doesn't let BeginRitualProxy through
                && (Multiplayer.ExecutingCmds || Multiplayer.Ticking))
            {
                var dialog = (Dialog_BeginRitual)window;
                dialog.PostOpen(); // Completes initialization

                var comp = dialog.map.MpComp();

                if (comp.ritualSession != null &&
                    (comp.ritualSession.data.ritual != dialog.ritual ||
                     comp.ritualSession.data.outcome != dialog.outcome))
                {
                    Messages.Message("MpAnotherRitualInProgress".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }

                if (comp.ritualSession == null)
                {
                    var data = new RitualData
                    {
                        ritual = dialog.ritual,
                        target = dialog.target,
                        obligation = dialog.obligation,
                        outcome = dialog.outcome,
                        extraInfos = dialog.extraInfos,
                        action = dialog.action,
                        ritualLabel = dialog.ritualLabel,
                        confirmText = dialog.confirmText,
                        organizer = dialog.organizer,
                        assignments = MpUtil.ShallowCopy(dialog.assignments, new MpRitualAssignments())
                    };

                    comp.CreateRitualSession(data);
                }

                if (TickPatch.currentExecutingCmdIssuedBySelf)
                    comp.ritualSession.OpenWindow();

                return false;
            }

            return true;
        }
    }

}
