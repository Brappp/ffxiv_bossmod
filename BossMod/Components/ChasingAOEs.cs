namespace BossMod.Components;

// generic 'chasing AOE' component - these are AOEs that follow the target for a set amount of casts
public class GenericChasingAOEs(BossModule module, Enum? aid = default, string warningText = "GTFO from chasing aoe!") : GenericAOEs(module, aid, warningText)
{
    public class Chaser(AOEShape shape, Actor target, WPos prevPos, float moveDist, int numRemaining, DateTime nextActivation, float secondsBetweenActivations, float expectedMoveDist)
    {
        public AOEShape Shape = shape;
        public Actor Target = target;
        public WPos PrevPos = prevPos;
        public float NextMoveDist = moveDist;
        public float ExpectedMoveDist = expectedMoveDist;
        public int NumRemaining = numRemaining;
        public DateTime NextActivation = nextActivation;
        public float SecondsBetweenActivations = secondsBetweenActivations;

        public WPos PredictedPosition()
        {
            var offset = Target.Position - PrevPos;
            var distance = offset.Length();
            return distance > NextMoveDist ? PrevPos + NextMoveDist * offset / distance : Target.Position;
        }
    }

    public List<Chaser> Chasers = [];

    public override IEnumerable<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        foreach (var c in Chasers)
        {
            var pos = c.PredictedPosition();
            var off = pos - c.PrevPos;
            yield return new(c.Shape, pos, off.LengthSq() > 0 ? Angle.FromDirection(off) : default, c.NextActivation);
        }
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        base.AddAIHints(slot, actor, assignment, hints);

        if (Chasers.FirstOrDefault(c => c.Target == actor) is { } chaser && chaser.Shape is AOEShapeCircle(float radius))
        {
            var zoneSizeMulti = chaser.NumRemaining > 1 ? 2 : 1;
            hints.AddForbiddenZone(new AOEShapeCircle(radius + chaser.ExpectedMoveDist * zoneSizeMulti), chaser.PrevPos, default, chaser.NextActivation);
        }
    }

    public override PlayerPriority CalcPriority(int pcSlot, Actor pc, int playerSlot, Actor player, ref uint customColor) => Chasers.Any(c => c.Target == player) ? PlayerPriority.Interesting : PlayerPriority.Irrelevant;

    // returns null if chaser was not found
    public Chaser? Advance(WPos pos, float moveDistance, DateTime currentTime, bool removeWhenFinished = true)
    {
        ++NumCasts;
        var c = Chasers.MinBy(c => (c.PredictedPosition() - pos).LengthSq());
        if (c == null)
            return null;

        if (--c.NumRemaining <= 0 && removeWhenFinished)
        {
            Chasers.Remove(c);
        }
        else
        {
            c.PrevPos = pos;
            c.NextMoveDist = moveDistance;
            c.NextActivation = currentTime.AddSeconds(c.SecondsBetweenActivations);
        }
        return c;
    }
}

// standard chasing aoe; first cast is long - assume it is baited on the nearest allowed target; successive casts are instant
public class StandardChasingAOEs(BossModule module, AOEShape shape, Enum actionFirst, Enum actionRest, float moveDistance, float secondsBetweenActivations, int maxCasts) : GenericChasingAOEs(module)
{
    public AOEShape Shape = shape;
    public ActionID ActionFirst = ActionID.MakeSpell(actionFirst);
    public ActionID ActionRest = ActionID.MakeSpell(actionRest);
    public float MoveDistance = moveDistance;
    public float SecondsBetweenActivations = secondsBetweenActivations;
    public int MaxCasts = maxCasts;
    public BitMask ExcludedTargets; // any targets in this mask aren't considered to be possible targets

    public override void Update()
    {
        Chasers.RemoveAll(c => (c.Target.IsDestroyed || c.Target.IsDead) && c.NumRemaining < MaxCasts);
    }

    public override void DrawArenaForeground(int pcSlot, Actor pc)
    {
        foreach (var c in Chasers)
        {
            if (Arena.Config.ShowOutlinesAndShadows)
                Arena.AddLine(c.PrevPos, c.Target.Position, 0xFF000000, 2);
            Arena.AddLine(c.PrevPos, c.Target.Position, ArenaColor.Danger);
        }
    }

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action == ActionFirst)
        {
            var pos = spell.TargetID == caster.InstanceID ? caster.Position : WorldState.Actors.Find(spell.TargetID)?.Position ?? spell.LocXZ;
            var (slot, target) = Raid.WithSlot().ExcludedFromMask(ExcludedTargets).MinBy(ip => (ip.Item2.Position - pos).LengthSq());
            if (target != null)
            {
                Chasers.Add(new(Shape, target, pos, 0, MaxCasts, Module.CastFinishAt(spell), SecondsBetweenActivations, MoveDistance)); // initial cast does not move anywhere
                ExcludedTargets.Set(slot);
            }
        }
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action == ActionFirst || spell.Action == ActionRest)
        {
            var pos = spell.MainTargetID == caster.InstanceID ? caster.Position : WorldState.Actors.Find(spell.MainTargetID)?.Position ?? spell.TargetXZ;
            var advanced = Advance(pos, MoveDistance, WorldState.CurrentTime);
            if (advanced == null)
                ReportError($"unexpected cast from chasing AOE at {pos}");
            if (advanced?.NumRemaining <= 0)
                ExcludedTargets.Clear(Raid.FindSlot(advanced.Target.InstanceID));
        }
    }
}
