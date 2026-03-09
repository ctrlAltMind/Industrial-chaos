using System;
using System.Collections.Generic;
using System.Linq;
using IndustrialChaos.Core.Models;
using IndustrialChaos.Core.Systems;

namespace IndustrialChaos.Core
{
    public enum LogLevel { Info, Ok, Warn, Danger }

    public class LogEntry
    {
        public int      Tick    { get; set; }
        public float    Time    { get; set; }
        public string   Message { get; set; }
        public LogLevel Level   { get; set; }
    }

    /// <summary>
    /// ============================================================
    /// INDUSTRIAL CHAOS — SimulationEngine v0.1
    /// ============================================================
    ///
    /// Motor principal da simulação. NÃO herda MonoBehaviour.
    /// Pode ser instanciado em testes unitários, threads ou coroutines Unity.
    ///
    /// Uso básico:
    ///   var engine = new SimulationEngine();
    ///   engine.Setup(config, machines, operators, contracts, rawBatch);
    ///   engine.Step();   // chamado a cada tick pelo Unity ou pelos testes
    ///
    /// Eventos:
    ///   engine.OnScrapGenerated  += (part, machine) => { ... };
    ///   engine.OnContractFailed  += (contract)      => { ... };
    ///
    /// ============================================================
    /// </summary>
    public class SimulationEngine
    {
        // ── Estado global ─────────────────────────────────────────────────
        public int    Tick          { get; private set; }
        public float  ShiftTime     { get; private set; }   // segundos simulados
        public bool   IsPaused      { get; private set; }
        public float  SpeedMultiplier { get; set; } = 1f;

        // ── Entidades ─────────────────────────────────────────────────────
        public List<Machine>       Machines    { get; private set; } = new();
        public List<Operator>      Operators   { get; private set; } = new();
        public List<Contract>      Contracts   { get; private set; } = new();
        public Queue<Part>         RawBuffer   { get; private set; } = new();
        public Queue<Part>         FinishedBuffer { get; private set; } = new();
        public List<MaterialBatch> Batches     { get; private set; } = new();

        // ── Config ────────────────────────────────────────────────────────
        public SimulationConfig Config { get; private set; } = new();

        // ── Acumuladores financeiros ───────────────────────────────────────
        public float Revenue    { get; private set; }
        public float ScrapCost  { get; private set; }
        public float EnergyCost { get; private set; }
        public float ToolCost   { get; private set; }
        public float NetProfit  => Revenue - ScrapCost - EnergyCost - ToolCost;

        // ── Contadores globais ────────────────────────────────────────────
        public int TotalPartsCompleted { get; internal set; }
        public int TotalScrap          { get; internal set; }
        public float OEE => (TotalPartsCompleted + TotalScrap) > 0
            ? (float)TotalPartsCompleted / (TotalPartsCompleted + TotalScrap) * 100f
            : 0f;

        // ── Log interno ───────────────────────────────────────────────────
        public List<LogEntry> Log          { get; } = new();
        public const int      LogMaxSize   = 500;

        // ── Subsistemas ───────────────────────────────────────────────────
        private ScrapSystem      _scrapSystem;
        private ProductionSystem _productionSystem;
        private OperatorSystem   _operatorSystem;

        // ── Eventos C# puros (sem UnityEvent) ─────────────────────────────
        // A camada de apresentação subscreve estes eventos.
        // O engine nunca sabe quem está a ouvir.

        public event Action<Part>                         OnPartCompleted;
        public event Action<Part, Machine>                OnScrapGenerated;
        public event Action<Machine>                      OnMachineFault;
        public event Action<Contract>                     OnContractFulfilled;
        public event Action<Contract>                     OnContractFailed;
        public event Action<Operator, OperatorEvent>      OnOperatorEvent;
        public event Action<LogEntry>                     OnLog;
        public event Action<int>                          OnTick;

        // ─────────────────────────────────────────────────────────────────
        // SETUP
        // ─────────────────────────────────────────────────────────────────

        public SimulationEngine() { }

        /// <summary>
        /// Inicializa o engine com todos os dados de jogo.
        /// Chamado uma vez antes do primeiro Step().
        /// </summary>
        public void Setup(
            SimulationConfig       config,
            List<Machine>          machines,
            List<Operator>         operators,
            List<Contract>         contracts,
            MaterialBatch          initialBatch,
            int?                   seed = null)
        {
            Config    = config;
            Machines  = machines;
            Operators = operators;
            Contracts = contracts;

            if (initialBatch != null)
                Batches.Add(initialBatch);

            // Preenche raw buffer com peças do primeiro contrato activo
            RefillRawBuffer();

            // Subsistemas (seed partilhada para determinismo em testes)
            _scrapSystem      = new ScrapSystem(this, seed);
            _productionSystem = new ProductionSystem(this, _scrapSystem);
            _operatorSystem   = new OperatorSystem(this, seed);

            RaiseLog("SimulationEngine inicializado", LogLevel.Ok);
        }

        // ─────────────────────────────────────────────────────────────────
        // TICK
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Avança um tick. Chamado pelo MonoBehaviour (via InvokeRepeating)
        /// ou directamente nos testes unitários.
        /// </summary>
        public void Step()
        {
            if (IsPaused) return;

            Tick++;
            float delta = Config.TickDuration * SpeedMultiplier;
            ShiftTime += delta;

            // Ordem de execução importa
            _operatorSystem.Process(delta);
            _productionSystem.Process(delta);
            ProcessContracts();
            ProcessFinishedBuffer();
            RefillRawBuffer();

            OnTick?.Invoke(Tick);
        }

        // ─────────────────────────────────────────────────────────────────
        // CONTRATOS
        // ─────────────────────────────────────────────────────────────────

        private void ProcessContracts()
        {
            foreach (var contract in Contracts.Where(c => c.Status == ContractStatus.Active))
            {
                contract.ProjectedDeliveryTick = CalculateProjectedDelivery(contract);

                // Verifica milestones
                foreach (var ms in contract.Milestones.Where(m => !m.IsFulfilled && !m.IsFailed))
                {
                    if (Tick >= ms.DeadlineTick && ms.Delivered < ms.QuantityRequired)
                    {
                        int missed     = ms.QuantityRequired - ms.Delivered;
                        float penalty  = missed * ms.PenaltyPerMissed;
                        ScrapCost     += penalty;
                        ms.IsFailed    = true;
                        RaiseLog($"{contract.Name}: milestone falhada — {missed} peças em falta — penalidade €{penalty:F0}", LogLevel.Danger);
                    }
                }

                // Contrato completo
                if (contract.TotalDelivered >= contract.TotalQuantity)
                {
                    contract.Status = ContractStatus.Fulfilled;
                    RaiseLog($"{contract.Name}: CUMPRIDO ✓ — receita €{contract.RevenueEarned:F0}", LogLevel.Ok);
                    OnContractFulfilled?.Invoke(contract);
                }

                // Deadline global ultrapassado sem cumprir
                bool pastDeadline = contract.Milestones.All(m => Tick > m.DeadlineTick);
                if (pastDeadline && contract.Status == ContractStatus.Active)
                {
                    contract.Status = ContractStatus.Failed;
                    RaiseLog($"{contract.Name}: FALHADO — deadline ultrapassado", LogLevel.Danger);
                    OnContractFailed?.Invoke(contract);
                }
            }
        }

        private void ProcessFinishedBuffer()
        {
            while (FinishedBuffer.Count > 0)
            {
                var part     = FinishedBuffer.Dequeue();
                var contract = Contracts.FirstOrDefault(c => c.Id == part.ContractId);
                if (contract == null || contract.Status != ContractStatus.Active) continue;

                if (!contract.AcceptsPart(part.Grade)) continue;

                float price = contract.PriceForGrade(part.Grade);
                contract.RevenueEarned += price;
                Revenue += price;
                TotalPartsCompleted++;

                if (part.Grade == QualityGrade.A) contract.DeliveredA++;
                else                               contract.DeliveredB++;

                // Actualiza milestone mais próxima não cumprida
                var ms = contract.Milestones.FirstOrDefault(m => !m.IsFulfilled && !m.IsFailed);
                if (ms != null) ms.Delivered++;

                RaisePartCompleted(part);
            }
        }

        private float CalculateProjectedDelivery(Contract contract)
        {
            float throughputPerTick = TotalPartsCompleted > 0
                ? (float)TotalPartsCompleted / Tick
                : 0f;
            if (throughputPerTick <= 0f) return float.MaxValue;
            return Tick + contract.Remaining / throughputPerTick;
        }

        // ─────────────────────────────────────────────────────────────────
        // RAW BUFFER
        // ─────────────────────────────────────────────────────────────────

        private void RefillRawBuffer()
        {
            if (RawBuffer.Count >= Config.RawBufferMax * 0.3f) return;

            var batch    = Batches.FirstOrDefault(b => !b.IsExhausted);
            var contract = Contracts.FirstOrDefault(c => c.Status == ContractStatus.Active);
            if (batch == null || contract == null) return;

            int toAdd = Math.Min(
                Config.RawBufferMax - RawBuffer.Count,
                batch.Remaining
            );

            for (int i = 0; i < toAdd; i++)
            {
                var part = batch.SpawnPart(contract.PartTypeId, contract.Id, Tick);
                if (part != null)
                {
                    // Atribui a rota com base no produto
                    if (Config.PartRoutes.TryGetValue(part.PartTypeId, out var route))
                        part.Route = new List<OperationType>(route);
                    RawBuffer.Enqueue(part);
                }
            }

            if (batch.IsExhausted)
                RaiseLog($"Lote {batch.Id} esgotado — qualidade foi {batch.Quality:P0}", LogLevel.Warn);
        }

        // ─────────────────────────────────────────────────────────────────
        // CONTROLO DE JOGO (API PÚBLICA)
        // ─────────────────────────────────────────────────────────────────

        public void Pause()  => IsPaused = true;
        public void Resume() => IsPaused = false;

        public void SetSpeed(float multiplier) =>
            SpeedMultiplier = Math.Clamp(multiplier, 0.1f, 10f);

        public void ChangeTool(Machine machine)
        {
            machine.ChangeTool();
            ToolCost += Config.ToolChangeCost;
            RaiseLog($"{machine.Id}: ferramenta trocada — custo €{Config.ToolChangeCost:F0}", LogLevel.Ok);
        }

        public void AssignOperator(Operator op, Machine machine)
        {
            // Remove de máquina anterior
            if (op.AssignedMachine != null)
                op.AssignedMachine.AssignedOperator = null;

            // Remove operador anterior da máquina destino
            if (machine.AssignedOperator != null)
                machine.AssignedOperator.AssignedMachine = null;

            op.AssignedMachine         = machine;
            machine.AssignedOperator   = op;

            RaiseLog($"{op.Name} alocado a {machine.Id}", LogLevel.Info);
        }

        public void UnassignOperator(Operator op)
        {
            if (op.AssignedMachine != null)
                op.AssignedMachine.AssignedOperator = null;
            op.AssignedMachine = null;
            RaiseLog($"{op.Name} removido da linha", LogLevel.Info);
        }

        public void EmergencyStop()
        {
            foreach (var m in Machines) m.State = MachineState.Paused;
            RaiseLog("PARAGEM DE EMERGÊNCIA — todas as máquinas paradas", LogLevel.Danger);
        }

        public void ResumeAll()
        {
            foreach (var m in Machines.Where(m => m.State == MachineState.Paused))
                m.State = MachineState.Idle;
            RaiseLog("Linha retomada", LogLevel.Ok);
        }

        public bool AcceptContract(Contract contract)
        {
            if (Contracts.Any(c => c.Status == ContractStatus.Active && c.PartTypeId != contract.PartTypeId))
            {
                RaiseLog($"Atenção: aceitar {contract.Name} com produto diferente vai criar curva de aprendizagem", LogLevel.Warn);
            }
            contract.Status = ContractStatus.Active;
            Contracts.Add(contract);
            RaiseLog($"Contrato {contract.Name} aceite — {contract.TotalQuantity} × {contract.PartTypeId}", LogLevel.Ok);
            return true;
        }

        public void AddMaterialBatch(MaterialBatch batch)
        {
            Batches.Add(batch);
            RaiseLog($"Lote de MP recebido — qualidade {batch.Quality:P0} ({batch.TotalParts} peças)", LogLevel.Info);
        }

        // ─────────────────────────────────────────────────────────────────
        // HELPERS (usados pelos subsistemas — internal)
        // ─────────────────────────────────────────────────────────────────

        internal bool IsFirstMachine(Machine m) => Machines.Count > 0 && Machines[0] == m;
        internal bool IsLastMachine(Machine m)  => Machines.Count > 0 && Machines[^1] == m;

        /// Retorna a primeira máquina disponível para um tipo de operação
        internal Machine GetMachineForOp(OperationType opType) =>
            Machines.FirstOrDefault(m => m.OpType == opType && m.IsOperational);

        internal bool HasUrgentDeadline()
        {
            foreach (var c in Contracts.Where(ct => ct.Status == ContractStatus.Active))
                foreach (var ms in c.Milestones.Where(m => !m.IsFulfilled))
                    if (ms.DeadlineTick - Tick < Config.UrgentDeadlineTicksRemaining)
                        return true;
            return false;
        }

        // ── Financeiros (chamados pelos subsistemas) ───────────────────────
        internal void AddRevenue(float amount)    => Revenue    += amount;
        internal void AddScrapCost(float amount)  => ScrapCost  += amount;
        internal void AddEnergyCost(float amount) => EnergyCost += amount;

        // ── Propagação de eventos ─────────────────────────────────────────
        internal void RaisePartCompleted(Part p)                       => OnPartCompleted?.Invoke(p);
        internal void RaiseScrap(Part p, Machine m)                    => OnScrapGenerated?.Invoke(p, m);
        internal void RaiseMachineFault(Machine m)                     => OnMachineFault?.Invoke(m);
        internal void RaiseContractFulfilled(Contract c)               => OnContractFulfilled?.Invoke(c);
        internal void RaiseContractFailed(Contract c)                  => OnContractFailed?.Invoke(c);
        internal void RaiseOperatorEvent(Operator op, OperatorEvent e) => OnOperatorEvent?.Invoke(op, e);

        internal void RaiseLog(string message, LogLevel level = LogLevel.Info)
        {
            var entry = new LogEntry { Tick = Tick, Time = ShiftTime, Message = message, Level = level };
            Log.Add(entry);
            if (Log.Count > LogMaxSize) Log.RemoveAt(0);
            OnLog?.Invoke(entry);
        }

        // ─────────────────────────────────────────────────────────────────
        // MÉTRICAS (para dashboard)
        // ─────────────────────────────────────────────────────────────────

        public SimulationMetrics GetMetrics()
        {
            float elapsedHours = ShiftTime / 3600f;
            return new SimulationMetrics
            {
                Tick                = Tick,
                ShiftTime           = ShiftTime,
                TotalPartsCompleted = TotalPartsCompleted,
                TotalScrap          = TotalScrap,
                OEE                 = OEE,
                ThroughputPerHour   = elapsedHours > 0 ? TotalPartsCompleted / elapsedHours : 0f,
                Revenue             = Revenue,
                ScrapCost           = ScrapCost,
                EnergyCost          = EnergyCost,
                NetProfit           = NetProfit,
                RawBufferCount      = RawBuffer.Count,
                FinishedBufferCount = FinishedBuffer.Count,
                MachineMetrics      = Machines.Select(m => new MachineMetrics
                {
                    Id              = m.Id,
                    State           = m.State,
                    ToolWear        = m.ToolWear,
                    ScrapRate       = m.ScrapRateThisShift,
                    PartsProcessed  = m.PartsProcessed,
                    BufferInCount   = m.BufferIn.Count,
                    BufferOutCount  = m.BufferOut.Count,
                    CycleProgress   = m.CycleTimeActual > 0 ? m.CycleProgress / m.CycleTimeActual : 0f,
                }).ToList(),
                OperatorMetrics     = Operators.Select(op => new OperatorMetrics
                {
                    Name            = op.Name,
                    Age             = op.Age,
                    IsPresent       = op.IsPresent,
                    IsAssigned      = op.IsAssigned,
                    AssignedMachine = op.AssignedMachine?.Id ?? "—",
                    Fatigue         = op.Fatigue,
                    GoodParts       = op.GoodPartsThisShift,
                }).ToList(),
            };
        }
    }

    // ── DTOs de métricas ──────────────────────────────────────────────────────

    public class SimulationMetrics
    {
        public int   Tick                { get; set; }
        public float ShiftTime           { get; set; }
        public int   TotalPartsCompleted { get; set; }
        public int   TotalScrap          { get; set; }
        public float OEE                 { get; set; }
        public float ThroughputPerHour   { get; set; }
        public float Revenue             { get; set; }
        public float ScrapCost           { get; set; }
        public float EnergyCost          { get; set; }
        public float NetProfit           { get; set; }
        public int   RawBufferCount      { get; set; }
        public int   FinishedBufferCount { get; set; }

        public List<MachineMetrics>  MachineMetrics  { get; set; }
        public List<OperatorMetrics> OperatorMetrics { get; set; }
    }

    public class MachineMetrics
    {
        public string       Id             { get; set; }
        public MachineState State          { get; set; }
        public float        ToolWear       { get; set; }
        public float        ScrapRate      { get; set; }
        public int          PartsProcessed { get; set; }
        public int          BufferInCount  { get; set; }
        public int          BufferOutCount { get; set; }
        public float        CycleProgress  { get; set; }  // 0–1
    }

    public class OperatorMetrics
    {
        public string Name            { get; set; }
        public int    Age             { get; set; }
        public bool   IsPresent       { get; set; }
        public bool   IsAssigned      { get; set; }
        public string AssignedMachine { get; set; }
        public float  Fatigue         { get; set; }
        public int    GoodParts       { get; set; }
    }
}
