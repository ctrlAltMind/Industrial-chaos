using System;
using System.Collections.Generic;

namespace IndustrialChaos.Core.Models
{
    public enum QualityOutcome { Pending, OK, Repairable, Scrap }
    public enum QualityGrade  { A, B, Scrap }

    /// <summary>
    /// Instância única de uma peça em produção.
    /// Criada quando sai do raw buffer, destruída quando entra no finished buffer ou scrap.
    /// </summary>
    public class Part
    {
        // ── Identidade ────────────────────────────────────────────────────
        public Guid   Id         { get; } = Guid.NewGuid();
        public string PartTypeId { get; set; }   // ex: "BRACKET-A7"
        public Guid   ContractId { get; set; }

        // ── Rota de produção ──────────────────────────────────────────────
        /// Sequência de operações definida pela ficha de processo do produto
        public List<OperationType> Route          { get; set; } = new();
        public int                 CurrentOpIndex { get; set; } = 0;

        public OperationType? CurrentOperation =>
            CurrentOpIndex < Route.Count ? Route[CurrentOpIndex] : null;

        public bool IsRouteComplete => CurrentOpIndex >= Route.Count;

        /// Avança para a próxima operação. Retorna true se ainda há operações.
        public bool AdvanceRoute()
        {
            CurrentOpIndex++;
            return !IsRouteComplete;
        }

        // ── Qualidade ─────────────────────────────────────────────────────
        public QualityOutcome Outcome      { get; set; } = QualityOutcome.Pending;
        public QualityGrade   Grade        { get; set; } = QualityGrade.A;
        public float          MaterialQuality { get; set; } = 1f;  // 0.6–1.0, do lote

        /// Risco acumulado ao longo das operações (soma dos contributos de cada máquina)
        public float AccumulatedScrapRisk { get; set; } = 0f;

        /// Histórico de scrap risk por operação — para dashboard de engenharia
        public List<(OperationType op, float risk)> ScrapHistory { get; } = new();

        // ── Tracking temporal ─────────────────────────────────────────────
        public int CreatedTick    { get; set; }
        public int CompletedTick  { get; set; } = -1;
        public int LeadTimeTicks  => CompletedTick >= 0 ? CompletedTick - CreatedTick : -1;

        // ── Reparação ─────────────────────────────────────────────────────
        public bool IsInRepairQueue { get; set; } = false;
        public int  RepairAttempts  { get; set; } = 0;

        public override string ToString() =>
            $"Part[{PartTypeId}] op={CurrentOpIndex}/{Route.Count} quality={Outcome} grade={Grade}";
    }
}
