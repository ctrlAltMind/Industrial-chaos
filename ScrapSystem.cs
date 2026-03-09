using System;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Systems
{
    /// <summary>
    /// Calcula scrap outcome por peça + operação.
    /// Fórmula auditável — jogador consegue sempre perceber a causa.
    ///
    /// scrap% = base_scrap
    ///        + wear_contribution       (linear até 80%, exponencial acima)
    ///        + process_variance        (ruído gaussiano ±2%)
    ///        + material_variance       (do lote de MP)
    ///        - skill_modifier          (operador efectivo)
    /// </summary>
    public class ScrapSystem
    {
        private readonly SimulationEngine _engine;
        private readonly Random _rng;

        // Constantes da fórmula — ajustáveis para balancing
        private const float WearLinearFactor      = 0.12f;  // % scrap por % wear (até 80)
        private const float WearExponentThreshold = 80f;
        private const float WearExponentFactor    = 0.008f; // aceleração exponencial acima de 80%
        private const float ProcessVarianceMax    = 2f;     // ±2%
        private const float SkillModMax           = 8f;     // operador perfeito reduz 8%
        private const float RepairableThreshold   = 0.6f;   // 60% dos scrap são reparáveis

        public ScrapSystem(SimulationEngine engine, int? seed = null)
        {
            _engine = engine;
            _rng    = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        // ── API principal ─────────────────────────────────────────────────

        /// Determina o outcome de uma peça após uma operação.
        /// Retorna true se é scrap (ou reparável).
        public QualityOutcome Evaluate(Part part, Machine machine)
        {
            float scrapChance = CalculateScrapChance(part, machine);

            // Regista no histórico da peça para auditoria
            part.ScrapHistory.Add((machine.OpType, scrapChance));
            part.AccumulatedScrapRisk += scrapChance * 0.1f; // contributo cumulativo

            float roll = (float)_rng.NextDouble() * 100f;

            if (roll >= scrapChance)
                return QualityOutcome.OK;

            // É scrap — mas será reparável?
            bool repairable = (float)_rng.NextDouble() < RepairableThreshold
                              && part.RepairAttempts == 0;  // só repara uma vez

            return repairable ? QualityOutcome.Repairable : QualityOutcome.Scrap;
        }

        /// Calcula a % de chance de scrap com todos os componentes decompostos.
        /// Público para os testes poderem inspecionar.
        public ScrapBreakdown CalculateBreakdown(Part part, Machine machine)
        {
            float effectiveSkill = GetEffectiveSkill(machine, part);

            return new ScrapBreakdown
            {
                BaseScrap         = machine.BaseScrapPct,
                WearContribution  = CalculateWearContribution(machine.ToolWear),
                ProcessVariance   = SampleProcessVariance(),
                MaterialVariance  = CalculateMaterialVariance(part.MaterialQuality),
                SkillModifier     = -(effectiveSkill / 100f) * SkillModMax,
                EffectiveSkill    = effectiveSkill,
                ToolWear          = machine.ToolWear,
            };
        }

        // ── Cálculo interno ───────────────────────────────────────────────

        private float CalculateScrapChance(Part part, Machine machine)
        {
            var b = CalculateBreakdown(part, machine);
            return Math.Max(0f, Math.Min(60f, b.Total));
        }

        private float CalculateWearContribution(float wear)
        {
            if (wear <= WearExponentThreshold)
                return wear * WearLinearFactor;

            // Acima de 80%: linear + componente exponencial
            float linearPart = WearExponentThreshold * WearLinearFactor;
            float excess      = wear - WearExponentThreshold;
            float expPart     = linearPart + (float)Math.Pow(excess, 1.8f) * WearExponentFactor;
            return expPart;
        }

        private float CalculateMaterialVariance(float materialQuality)
        {
            // qualidade 1.0 → 0% penalidade | qualidade 0.6 → +4%
            return (1f - materialQuality) * 10f;
        }

        private float SampleProcessVariance()
        {
            // Aproximação gaussiana via Box-Muller
            double u1   = 1.0 - _rng.NextDouble();
            double u2   = 1.0 - _rng.NextDouble();
            double rand = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            return (float)(rand * ProcessVarianceMax * 0.5f); // ±2% (stddev=1%)
        }

        private float GetEffectiveSkill(Machine machine, Part part)
        {
            if (machine.AssignedOperator == null) return 15f; // sem operador → skill mínima

            return machine.AssignedOperator.GetEffectiveSkill(machine.OpType, part.PartTypeId);
        }
    }

    // ── DTO para auditoria / dashboard ───────────────────────────────────────

    /// Decomposição completa do cálculo de scrap.
    /// Enviado ao dashboard para o jogador perceber a causa.
    public class ScrapBreakdown
    {
        public float BaseScrap        { get; set; }
        public float WearContribution { get; set; }
        public float ProcessVariance  { get; set; }
        public float MaterialVariance { get; set; }
        public float SkillModifier    { get; set; }  // negativo (reduz scrap)

        // Contexto adicional para o log
        public float EffectiveSkill   { get; set; }
        public float ToolWear         { get; set; }

        public float Total => BaseScrap + WearContribution + ProcessVariance
                            + MaterialVariance + SkillModifier;

        public string Summarize() =>
            $"Scrap {Total:F1}% = base {BaseScrap:F1} + wear {WearContribution:F1} " +
            $"+ variance {ProcessVariance:+0.0;-0.0} + material {MaterialVariance:F1} " +
            $"- skill {-SkillModifier:F1} (skill_ef={EffectiveSkill:F0})";
    }
}
