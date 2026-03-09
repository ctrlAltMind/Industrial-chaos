using System;
using System.Collections.Generic;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Models
{
    public enum OperatorEventType
    {
        Absent,          // não compareceu
        ProductivityDrop,// rendimento abaixo do normal
        Overtime,        // trabalhou mais — dedicação
        SkillGain,       // subiu skill
        FatigueWarning,  // fadiga >70%
        Mentoring,       // veterano a ensinar
        FaultPrevented   // experiência evitou avaria
    }

    public class OperatorEvent
    {
        public OperatorEventType Type    { get; set; }
        public string            Message { get; set; }
        public int               Tick    { get; set; }
    }

    /// <summary>
    /// Operador com skills, personalidade e estado dinâmico.
    /// Não herda MonoBehaviour.
    /// </summary>
    public class Operator
    {
        // ── Identidade ────────────────────────────────────────────────────
        public string Id   { get; set; }
        public string Name { get; set; }
        public int    Age  { get; set; }

        // ── Skills base (0–100 por tipo de operação) ───────────────────────
        public Dictionary<OperationType, float> Skills { get; set; } = new()
        {
            { OperationType.CNC,   20f },
            { OperationType.Weld,  20f },
            { OperationType.Rect,  20f },
            { OperationType.Grind, 20f },
            { OperationType.Insp,  20f },
            { OperationType.Rep,   20f },
            { OperationType.Pack,  20f },
        };

        // ── Curva de aprendizagem por produto (0–1) ───────────────────────
        /// Começa em 0.3 para produtos novos, sobe até 1.0 com experiência
        public Dictionary<string, float> ProductLearning { get; set; } = new();

        public float GetProductLearning(string partTypeId) =>
            ProductLearning.TryGetValue(partTypeId, out var v) ? v : 0.3f;

        // ── Fadiga ────────────────────────────────────────────────────────
        public float Fatigue        { get; set; } = 0f;   // 0–100
        public float FatigueRate    { get; set; } = 1.2f; // % por hora simulada
        public float FatigueRecoveryRate { get; set; } = 8f; // % por hora de descanso

        // ── Skill efectiva (o que realmente afecta a simulação) ────────────
        /// skill_efectiva = skill_base × (1 - fadiga×0.5) × curva_aprendizagem
        public float GetEffectiveSkill(OperationType opType, string partTypeId)
        {
            float skillBase   = Skills.TryGetValue(opType, out var s) ? s : 20f;
            float fatiguemod  = 1f - (Fatigue / 100f) * 0.5f;
            float learningMod = GetProductLearning(partTypeId);
            return skillBase * fatiguemod * learningMod;
        }

        // ── Personalidade ─────────────────────────────────────────────────
        public PersonalityProfile Personality { get; set; } = new();

        // ── Estado actual ─────────────────────────────────────────────────
        public Machine AssignedMachine   { get; set; } = null;
        public bool    IsPresent         { get; set; } = true;
        public bool    IsAssigned        => AssignedMachine != null && IsPresent;

        // ── Acumuladores do turno ─────────────────────────────────────────
        public int   PartsThisShift      { get; set; } = 0;
        public int   GoodPartsThisShift  { get; set; } = 0;
        public float HoursWorkedThisShift{ get; set; } = 0f;

        // ── Ganho de skill por peça boa ───────────────────────────────────
        public const float XpPerGoodPart        = 0.02f;
        public const float XpProductLearningRate = 0.015f;

        public void RegisterGoodPart(OperationType opType, string partTypeId)
        {
            PartsThisShift++;
            GoodPartsThisShift++;

            // skill base sobe lentamente
            if (Skills.TryGetValue(opType, out float current))
                Skills[opType] = Math.Min(100f, current + XpPerGoodPart);

            // curva de aprendizagem do produto sobe mais rápido
            float learning = GetProductLearning(partTypeId);
            ProductLearning[partTypeId] = Math.Min(1f, learning + XpProductLearningRate);
        }

        public void RegisterScrap() => PartsThisShift++;

        // ── Veterano: bónus de mentoria ───────────────────────────────────
        public bool  IsMentor       => Age >= 55;
        public float MentoringBonus => IsMentor ? 1.5f : 1f;  // XP multiplier para júniores próximos

        public void ResetShiftCounters()
        {
            PartsThisShift      = 0;
            GoodPartsThisShift  = 0;
            HoursWorkedThisShift= 0f;
        }

        public override string ToString() =>
            $"Operator[{Name}] age={Age} fatigue={Fatigue:F0}% present={IsPresent} machine={AssignedMachine?.Id ?? "—"}";
    }

    /// <summary>
    /// Traços de personalidade. Alguns visíveis ao jogador, outros revelados com tempo.
    /// Geram eventos probabilísticos no OperatorSystem.
    /// </summary>
    public class PersonalityProfile
    {
        // ── Traços visíveis desde o início ───────────────────────────────
        public float Dedication    { get; set; } = 0.5f;  // 0–1: probabilidade de overtime
        public float Reliability   { get; set; } = 0.5f;  // 0–1: inverso de prob. ausência
        public float StressTolerance { get; set; } = 0.5f;// 0–1: resiste a deadlines apertados

        // ── Traços ocultos (revelados por eventos) ────────────────────────
        public bool  HasDebt          { get; set; } = false; // trabalha mais, mas colapsa ocasionalmente
        public bool  HasSocialHabits  { get; set; } = false; // futebol, saídas — afecta segunda-feira
        public bool  HasFamilyStress  { get; set; } = false; // ausências pontuais imprevisíveis

        // ── Probabilidades derivadas (calculadas por OperatorSystem) ──────

        /// Chance por turno de fazer horas extra espontaneamente
        public float OvertimeChance =>
            Dedication * (HasDebt ? 1.4f : 1f);

        /// Chance por turno de não aparecer
        public float AbsenceChance =>
            (1f - Reliability) * (HasSocialHabits ? 1.3f : 1f) * (HasFamilyStress ? 1.2f : 1f);

        /// Modificador de produtividade após evento social (dia seguinte)
        public float PostSocialPenalty => HasSocialHabits ? 0.6f : 1f; // skill efectiva × 0.6

        /// Resistência à fadiga sob pressão
        public float FatigueResistance => StressTolerance * 0.4f; // reduz FatigueRate
    }
}
