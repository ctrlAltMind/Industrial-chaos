using System;
using System.Collections.Generic;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Models
{
    public enum ContractStatus { Active, Fulfilled, Failed, Negotiating, Pending }
    public enum QualityRequirement { OnlyA, AandB }  // só OK, ou aceita reparadas

    /// <summary>
    /// Entrega parcial dentro de um contrato.
    /// Jogador tem de cumprir cadência, não só total final.
    /// </summary>
    public class DeliveryMilestone
    {
        public int   QuantityRequired { get; set; }
        public int   DeadlineTick     { get; set; }
        public int   Delivered        { get; set; } = 0;
        public bool  IsFulfilled      => Delivered >= QuantityRequired;
        public bool  IsFailed         { get; set; } = false;
        public float PenaltyPerMissed { get; set; } = 8f;
    }

    public class Contract
    {
        // ── Identidade ────────────────────────────────────────────────────
        public Guid   Id         { get; } = Guid.NewGuid();
        public string Name       { get; set; }   // "CONTRACT-001"
        public string ClientName { get; set; }   // "Moldes Ferreira Lda"
        public string PartTypeId { get; set; }   // "BRACKET-A7"

        // ── Especificação técnica ─────────────────────────────────────────
        public List<OperationType>  RequiredRoute       { get; set; } = new();
        public QualityRequirement   QualityReq          { get; set; } = QualityRequirement.OnlyA;
        public float                ToleranceMm         { get; set; } = 0.05f;

        // ── Quantidades e prazos ──────────────────────────────────────────
        public int                       TotalQuantity { get; set; }
        public List<DeliveryMilestone>   Milestones    { get; set; } = new();

        // ── Financeiro ────────────────────────────────────────────────────
        public float PricePerPartA      { get; set; } = 21f;
        public float PricePerPartB      { get; set; } = 14f;  // grade B, se aceite
        public float PenaltyPerMissed   { get; set; } = 8f;
        public float PenaltyQuality     { get; set; } = 15f;  // por peça abaixo da spec

        // ── Estado ───────────────────────────────────────────────────────
        public ContractStatus Status        { get; set; } = ContractStatus.Pending;
        public int            DeliveredA    { get; set; } = 0;
        public int            DeliveredB    { get; set; } = 0;
        public float          RevenueEarned { get; set; } = 0f;
        public float          PenaltiesPaid { get; set; } = 0f;

        public int TotalDelivered => DeliveredA + DeliveredB;
        public int Remaining      => TotalQuantity - TotalDelivered;

        // ── Projecção (calculada pelo ContractSystem) ─────────────────────
        public float ProjectedDeliveryTick { get; set; } = -1f;
        public bool  IsOnTrack             { get; set; } = true;

        public bool AcceptsPart(QualityGrade grade) =>
            grade == QualityGrade.A ||
            (grade == QualityGrade.B && QualityReq == QualityRequirement.AandB);

        public float PriceForGrade(QualityGrade grade) =>
            grade == QualityGrade.A ? PricePerPartA : PricePerPartB;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lote de matéria prima. Qualidade varia por fornecimento.
    /// Afecta o scrap floor de todas as peças deste lote.
    /// </summary>
    public class MaterialBatch
    {
        public Guid   Id          { get; } = Guid.NewGuid();
        public string SupplierId  { get; set; }
        public string PartTypeId  { get; set; }

        /// 0.6–1.0. Afecta material_variance na fórmula de scrap.
        /// Jogador vê este valor no dashboard — pode rejeitar lote antes de usar.
        public float Quality      { get; set; } = 1f;

        public int   TotalParts   { get; set; } = 100;
        public int   Remaining    { get; set; } = 100;
        public float CostPerPart  { get; set; } = 3.5f;

        public bool IsExhausted   => Remaining <= 0;

        /// Contributo para material_variance na fórmula de scrap.
        /// Lote de qualidade 1.0 → sem penalidade. Qualidade 0.6 → +4% scrap.
        public float ScrapContribution => (1f - Quality) * 10f;  // 0–4%

        public Part SpawnPart(string partTypeId, Guid contractId, int currentTick)
        {
            if (IsExhausted) return null;
            Remaining--;
            return new Part
            {
                PartTypeId      = partTypeId,
                ContractId      = contractId,
                MaterialQuality = Quality,
                CreatedTick     = currentTick,
            };
        }
    }
}
