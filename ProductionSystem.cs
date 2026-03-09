using System.Collections.Generic;
using System.Linq;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Systems
{
    /// <summary>
    /// Processa o fluxo de peças através das máquinas a cada tick.
    /// Gere: ciclos de produção, buffers, routing, estado de máquinas.
    /// </summary>
    public class ProductionSystem
    {
        private readonly SimulationEngine _engine;
        private readonly ScrapSystem      _scrap;

        public ProductionSystem(SimulationEngine engine, ScrapSystem scrap)
        {
            _engine = engine;
            _scrap  = scrap;
        }

        // ── Tick principal ────────────────────────────────────────────────

        public void Process(float deltaTime)
        {
            foreach (var machine in _engine.Machines)
            {
                if (!machine.IsOperational) continue;

                UpdateMachineState(machine);
                if (machine.State == MachineState.Running)
                    AdvanceCycle(machine, deltaTime);
            }

            // Energia consumida
            foreach (var m in _engine.Machines.Where(m => m.State == MachineState.Running))
            {
                float kWhThisTick = m.EnergyKw * (deltaTime / 3600f);
                m.EnergyConsumed += kWhThisTick;
                _engine.AddEnergyCost(kWhThisTick * _engine.Config.EnergyPricePerKwh);
            }
        }

        // ── Estado da máquina ─────────────────────────────────────────────

        private void UpdateMachineState(Machine machine)
        {
            // Já tem peça em processo → continua Running
            if (machine.PartInProcess != null)
            {
                machine.State = MachineState.Running;
                return;
            }

            bool hasInput  = HasInput(machine);
            bool hasOutput = HasOutputSpace(machine);

            if (!hasInput)  { machine.State = MachineState.Starved; return; }
            if (!hasOutput) { machine.State = MachineState.Blocked;  return; }

            // Tudo livre → inicia novo ciclo
            machine.PartInProcess = ConsumeInput(machine);
            machine.CycleProgress = 0f;
            machine.State         = MachineState.Running;
        }

        private bool HasInput(Machine machine)
        {
            // Primeira máquina da linha lê do RawBuffer do engine
            if (_engine.IsFirstMachine(machine))
                return _engine.RawBuffer.Count > 0;

            return machine.BufferIn.Count > 0;
        }

        private bool HasOutputSpace(Machine machine)
        {
            if (_engine.IsLastMachine(machine))
                return _engine.FinishedBuffer.Count < _engine.Config.FinishedBufferMax;

            return machine.BufferOut.Count < machine.BufferMax;
        }

        private Part ConsumeInput(Machine machine)
        {
            if (_engine.IsFirstMachine(machine))
            {
                var part = _engine.RawBuffer.Dequeue();
                // Define a rota da peça com base no produto/contrato
                AssignRoute(part, machine);
                return part;
            }
            return machine.BufferIn.Dequeue();
        }

        // ── Routing ───────────────────────────────────────────────────────

        /// Atribui a rota de produção à peça com base no PartType registado nos configs
        private void AssignRoute(Part part, Machine firstMachine)
        {
            if (_engine.Config.PartRoutes.TryGetValue(part.PartTypeId, out var route))
                part.Route = new List<OperationType>(route);
            else
                part.Route = new List<OperationType> { OperationType.CNC, OperationType.Insp, OperationType.Pack };

            part.CurrentOpIndex = 0;
        }

        // ── Ciclo de produção ─────────────────────────────────────────────

        private void AdvanceCycle(Machine machine, float deltaTime)
        {
            machine.CycleProgress += deltaTime;

            if (machine.CycleProgress < machine.CycleTimeActual) return;

            // Ciclo completo
            CompleteCycle(machine);
        }

        private void CompleteCycle(Machine machine)
        {
            var part = machine.PartInProcess;
            machine.PartInProcess = null;
            machine.CycleProgress = 0f;

            // Tool wear
            machine.ToolWear += machine.WearPerPart;
            if (machine.ToolWear >= machine.FaultThreshold)
            {
                machine.State = MachineState.Fault;
                machine.FaultCount++;
                _engine.RaiseLog($"{machine.Id}: AVARIA — tool wear {machine.ToolWear:F0}%", LogLevel.Danger);
                _engine.RaiseMachineFault(machine);
                // Peça em processo vai para scrap
                RegisterScrap(part, machine, forced: true);
                return;
            }

            machine.PartsProcessed++;

            // Avalia scrap
            var outcome = _scrap.Evaluate(part, machine);
            var breakdown = _scrap.CalculateBreakdown(part, machine);

            switch (outcome)
            {
                case QualityOutcome.OK:
                    HandleGoodPart(part, machine, breakdown);
                    break;

                case QualityOutcome.Repairable:
                    HandleRepairablePart(part, machine, breakdown);
                    break;

                case QualityOutcome.Scrap:
                    RegisterScrap(part, machine);
                    break;
            }

            // Skill gain do operador
            if (outcome == QualityOutcome.OK)
                machine.AssignedOperator?.RegisterGoodPart(machine.OpType, part.PartTypeId);
            else
                machine.AssignedOperator?.RegisterScrap();

            machine.RecordHistory();
        }

        private void HandleGoodPart(Part part, Machine machine, ScrapBreakdown breakdown)
        {
            part.Outcome = QualityOutcome.OK;
            part.Grade   = QualityGrade.A;

            bool routeComplete = !part.AdvanceRoute();

            if (routeComplete || _engine.IsLastMachine(machine))
            {
                // Peça terminada
                part.CompletedTick = _engine.Tick;
                _engine.FinishedBuffer.Enqueue(part);
                _engine.RaisePartCompleted(part);
                _engine.RaiseLog(
                    $"{machine.Id}: peça OK → finished buffer | {breakdown.Summarize()}",
                    LogLevel.Ok);
            }
            else
            {
                // Avança para próxima máquina na rota
                var nextMachine = _engine.GetMachineForOp(part.CurrentOperation.Value);
                if (nextMachine != null)
                    nextMachine.BufferIn.Enqueue(part);
                else
                    machine.BufferOut.Enqueue(part); // fallback: buffer out da máquina
            }
        }

        private void HandleRepairablePart(Part part, Machine machine, ScrapBreakdown breakdown)
        {
            part.Outcome         = QualityOutcome.Repairable;
            part.IsInRepairQueue = true;
            part.RepairAttempts++;

            machine.ScrapCount++; // conta como scrap inicial
            _engine.AddScrapCost(_engine.Config.ScrapCostPerPart * 0.3f); // custo parcial

            _engine.RaiseLog(
                $"{machine.Id}: REPARÁVEL — {breakdown.Summarize()}",
                LogLevel.Warn);

            // Envia para fila de reparação (máquina REP)
            var repMachine = _engine.GetMachineForOp(OperationType.Rep);
            if (repMachine != null)
            {
                part.Route.Insert(part.CurrentOpIndex, OperationType.Rep);
                repMachine.BufferIn.Enqueue(part);
            }
            else
            {
                // Sem máquina de reparação → scrap total
                RegisterScrap(part, machine);
            }

            _engine.RaiseScrap(part, machine);
        }

        private void RegisterScrap(Part part, Machine machine, bool forced = false)
        {
            part.Outcome = QualityOutcome.Scrap;
            part.Grade   = QualityGrade.Scrap;
            machine.ScrapCount++;

            _engine.AddScrapCost(_engine.Config.ScrapCostPerPart);
            _engine.TotalScrap++;

            string reason = forced ? "FAULT" : "SCRAP";
            _engine.RaiseLog($"{machine.Id}: {reason} — peça perdida", LogLevel.Danger);
            _engine.RaiseScrap(part, machine);
        }
    }
}
