using System;
using System.Collections.Generic;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Models
{
    public enum MachineState
    {
        Idle,       // sem peça, à espera
        Running,    // a processar
        Blocked,    // buffer output cheio
        Starved,    // buffer input vazio
        Paused,     // parado manualmente ou por regra
        Fault       // avaria — requer intervenção
    }

    public enum OperationType { CNC, Weld, Rect, Grind, Insp, Rep, Pack }

    /// <summary>
    /// Representa uma máquina física na linha de produção.
    /// Não herda MonoBehaviour — lógica pura.
    /// </summary>
    public class Machine
    {
        // ── Identidade ────────────────────────────────────────────────────
        public string        Id          { get; set; }   // "CNC-01"
        public OperationType OpType      { get; set; }
        public MachineState  State       { get; set; } = MachineState.Idle;

        // ── Ferramenta ────────────────────────────────────────────────────
        /// 0–100. Afecta scrap linearmente até 80%, depois exponencialmente.
        public float ToolWear     { get; set; } = 0f;
        public float WearPerPart  { get; set; } = 0.18f;  // % por peça processada
        public float FaultThreshold { get; set; } = 98f;  // wear acima disto → FAULT

        // ── Ciclo de produção ─────────────────────────────────────────────
        public float CycleTimeBase    { get; set; } = 4f;   // segundos simulados
        public float CycleTimeActual  { get; set; } = 4f;   // pode ser alterado por regras
        public float CycleProgress    { get; set; } = 0f;
        public Part  PartInProcess    { get; set; } = null;

        // ── Buffers ───────────────────────────────────────────────────────
        public Queue<Part> BufferIn  { get; } = new();
        public Queue<Part> BufferOut { get; } = new();
        public int         BufferMax { get; set; } = 20;

        public float BufferInFillPct  => BufferIn.Count  / (float)BufferMax;
        public float BufferOutFillPct => BufferOut.Count / (float)BufferMax;

        // ── Operador ──────────────────────────────────────────────────────
        public Operator AssignedOperator { get; set; } = null;

        // ── Configuração de scrap ─────────────────────────────────────────
        public float BaseScrapPct { get; set; } = 3f;   // % mínima sem wear nem skill

        // ── Energia ───────────────────────────────────────────────────────
        public float EnergyKw { get; set; } = 2.4f;     // consumo quando RUNNING

        // ── Acumuladores do turno ─────────────────────────────────────────
        public int   PartsProcessed { get; set; } = 0;
        public int   ScrapCount     { get; set; } = 0;
        public int   FaultCount     { get; set; } = 0;
        public float EnergyConsumed { get; set; } = 0f;  // kWh

        // ── Histórico (para sparklines no dashboard) ───────────────────────
        public List<float> ScrapRateHistory    { get; } = new();
        public List<float> CycleTimeHistory    { get; } = new();
        public const int   HistoryMaxPoints    = 60;

        // ── Propriedades derivadas ─────────────────────────────────────────
        public float ScrapRateThisShift =>
            PartsProcessed > 0 ? (float)ScrapCount / PartsProcessed * 100f : 0f;

        public bool IsOperational =>
            State != MachineState.Fault && State != MachineState.Paused;

        // ── Métodos de estado ─────────────────────────────────────────────
        public void ChangeTool()
        {
            ToolWear = 0f;
            State    = MachineState.Idle;
        }

        public void ResetShiftCounters()
        {
            PartsProcessed = 0;
            ScrapCount     = 0;
            EnergyConsumed = 0f;
            ScrapRateHistory.Clear();
            CycleTimeHistory.Clear();
        }

        public void RecordHistory()
        {
            ScrapRateHistory.Add(ScrapRateThisShift);
            CycleTimeHistory.Add(CycleTimeActual);
            if (ScrapRateHistory.Count > HistoryMaxPoints)
                ScrapRateHistory.RemoveAt(0);
            if (CycleTimeHistory.Count > HistoryMaxPoints)
                CycleTimeHistory.RemoveAt(0);
        }

        public override string ToString() =>
            $"Machine[{Id}] state={State} wear={ToolWear:F1}% scrap={ScrapRateThisShift:F1}%";
    }
}
