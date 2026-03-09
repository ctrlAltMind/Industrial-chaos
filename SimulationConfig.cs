using System.Collections.Generic;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core
{
    /// <summary>
    /// Todos os parâmetros configuráveis da simulação num único lugar.
    /// Em Unity será um ScriptableObject. Aqui é um POCO para os testes.
    /// </summary>
    public class SimulationConfig
    {
        // ── Tempo ─────────────────────────────────────────────────────────
        public float TickDuration       { get; set; } = 0.25f;  // segundos reais por tick
        public float ShiftDurationHours { get; set; } = 8f;

        // ── Energia ───────────────────────────────────────────────────────
        public float EnergyPricePerKwh  { get; set; } = 0.18f;  // €/kWh

        // ── Financeiro ────────────────────────────────────────────────────
        public float ScrapCostPerPart   { get; set; } = 4.5f;   // custo por peça scrapeada
        public float ToolChangeCost     { get; set; } = 12f;    // custo de troca de ferramenta

        // ── Buffers globais ───────────────────────────────────────────────
        public int RawBufferMax       { get; set; } = 100;
        public int FinishedBufferMax  { get; set; } = 100;

        // ── Rotas por produto ─────────────────────────────────────────────
        /// Mapa: PartTypeId → sequência de operações
        public Dictionary<string, List<OperationType>> PartRoutes { get; set; } = new()
        {
            // Família A — Simples
            ["PIN-STD"]      = new() { OperationType.CNC, OperationType.Insp, OperationType.Pack },
            ["BRACKET-A7"]   = new() { OperationType.CNC, OperationType.CNC, OperationType.Insp, OperationType.Pack },
            ["SHAFT-GROUND"]  = new() { OperationType.CNC, OperationType.Grind, OperationType.Insp, OperationType.Pack },

            // Família B — Soldadas
            ["BRACKET-WELD"] = new() { OperationType.CNC, OperationType.Weld, OperationType.Insp, OperationType.Pack },
            ["STRUCT-B4"]    = new() { OperationType.CNC, OperationType.Weld, OperationType.Grind, OperationType.Insp, OperationType.Pack },

            // Família C — Alta precisão
            ["SHAFT-PREC"]   = new() { OperationType.CNC, OperationType.Rect, OperationType.Insp, OperationType.Pack },
            ["HOUSING-C4"]   = new() { OperationType.CNC, OperationType.Weld, OperationType.Rect, OperationType.Grind, OperationType.Insp, OperationType.Pack },
        };

        // ── Configs por tipo de máquina (defaults) ────────────────────────
        public Dictionary<OperationType, MachineTypeConfig> MachineDefaults { get; set; } = new()
        {
            [OperationType.CNC]   = new() { BaseScrap=3f, WearPerPart=0.18f, CycleTime=4.5f, EnergyKw=2.4f },
            [OperationType.Weld]  = new() { BaseScrap=5f, WearPerPart=0.22f, CycleTime=6.5f, EnergyKw=4.1f },
            [OperationType.Rect]  = new() { BaseScrap=2f, WearPerPart=0.15f, CycleTime=5.5f, EnergyKw=3.2f },
            [OperationType.Grind] = new() { BaseScrap=1.5f, WearPerPart=0.10f, CycleTime=3f, EnergyKw=1.8f },
            [OperationType.Insp]  = new() { BaseScrap=1f, WearPerPart=0.08f, CycleTime=3f, EnergyKw=0.8f },
            [OperationType.Rep]   = new() { BaseScrap=0f, WearPerPart=0.05f, CycleTime=9f, EnergyKw=1.2f },
            [OperationType.Pack]  = new() { BaseScrap=0.5f, WearPerPart=0.02f, CycleTime=1.5f, EnergyKw=0.4f },
        };

        // ── Threshold de deadline urgente (para stress dos operadores) ────
        public int UrgentDeadlineTicksRemaining { get; set; } = 480; // ~2h simuladas
    }

    public class MachineTypeConfig
    {
        public float BaseScrap  { get; set; }
        public float WearPerPart { get; set; }
        public float CycleTime  { get; set; }
        public float EnergyKw   { get; set; }
    }
}
