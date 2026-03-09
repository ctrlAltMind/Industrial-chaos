using System;
using System.Collections.Generic;
using System.Linq;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Systems
{
    // =========================================================================
    // INDUSTRIAL CHAOS — AutomationSystem v0.1
    // =========================================================================
    //
    // Sistema de regras IF/THEN configurável pelo jogador.
    // Filosofia: poder sem complexidade. O jogador escreve lógica reactiva
    // sem precisar de linguagem de programação.
    //
    // Estrutura de uma regra:
    //   Condition (o quê avaliar) → Comparator → Threshold → Action (o que fazer)
    //
    // Exemplo real:
    //   IF tool_wear(CNC-01) > 80 THEN change_tool(CNC-01)
    //   IF buffer_out(WELD-01) > 85% THEN slow_cycle(WELD-01, factor=0.7)
    //   IF scrap_rate(line) > 15% THEN alert("Scrap alto") + stop_line
    //
    // Regras têm:
    //   - cooldown    → não dispara repetidamente no mesmo segundo
    //   - enabled     → jogador pode ligar/desligar
    //   - fire_count  → auditável no dashboard
    //   - last_fired  → tick em que disparou pela última vez
    // =========================================================================

    // ── Enums públicos ────────────────────────────────────────────────────────

    /// O que a condição avalia
    public enum ConditionMetric
    {
        ToolWear,           // machine.ToolWear (0–100)
        BufferOutFill,      // machine.BufferOut.Count / BufferMax (0–100%)
        BufferInFill,       // machine.BufferIn.Count / BufferMax (0–100%)
        RawBufferFill,      // engine.RawBuffer.Count / RawBufferMax (0–100%)
        ScrapRateShift,     // machine.ScrapRateThisShift (0–100%)
        ScrapRateGlobal,    // engine total scrap rate (0–100%)
        OperatorFatigue,    // operator.Fatigue (0–100)
        CycleTimeDeviation, // desvio do ciclo actual vs base (%)
        DeadlineTicksLeft,  // ticks restantes no contrato mais urgente
        ThroughputPerHour,  // peças/hora ao ritmo actual
        MachineState,       // compara com MachineStateValue
    }

    /// Operadores de comparação
    public enum Comparator
    {
        GreaterThan,
        GreaterOrEqual,
        LessThan,
        LessOrEqual,
        Equal,
        NotEqual,
    }

    /// O que a acção executa
    public enum ActionType
    {
        ChangeTool,         // troca ferramenta da máquina alvo
        PauseMachine,       // pausa máquina alvo
        ResumeMachine,      // retoma máquina alvo
        SlowCycle,          // reduz CycleTimeActual por factor
        RestoreCycle,       // restaura CycleTimeActual para Base
        StopLine,           // para todas as máquinas
        Alert,              // adiciona entrada no log (sem acção física)
        AlertAndStop,       // alerta + para linha
        ChangeToolAndAlert, // troca ferramenta + alerta
        SetCycleTimeFactor, // define CycleTimeActual = Base × factor
    }

    // ── Condition ────────────────────────────────────────────────────────────

    /// Define o lado esquerdo do IF: o quê medir e em quê
    public class RuleCondition
    {
        /// Qual métrica avaliar
        public ConditionMetric Metric { get; set; }

        /// ID da máquina alvo (null = global/linha)
        public string MachineId { get; set; } = null;

        /// ID do operador alvo (para OperatorFatigue)
        public string OperatorId { get; set; } = null;

        /// Operador de comparação
        public Comparator Comparator { get; set; } = Comparator.GreaterThan;

        /// Valor de threshold para comparação numérica
        public float Threshold { get; set; }

        /// Para MachineState: o estado a comparar
        public MachineState StateValue { get; set; } = MachineState.Fault;

        public override string ToString() =>
            $"{Metric}({MachineId ?? "global"}) {Comparator} {Threshold}";
    }

    // ── Action ───────────────────────────────────────────────────────────────

    /// Define o lado direito do THEN: o que executar
    public class RuleAction
    {
        public ActionType ActionType { get; set; }

        /// Máquina alvo da acção (null = mesma da condição, ou todas)
        public string TargetMachineId { get; set; } = null;

        /// Factor para SlowCycle / SetCycleTimeFactor (0.1–2.0)
        public float CycleFactor { get; set; } = 0.7f;

        /// Mensagem para alertas
        public string AlertMessage { get; set; } = "";

        public override string ToString() =>
            $"{ActionType}({TargetMachineId ?? "same"}" +
            (CycleFactor != 0.7f ? $", factor={CycleFactor}" : "") +
            (!string.IsNullOrEmpty(AlertMessage) ? $", \"{AlertMessage}\"" : "") + ")";
    }

    // ── Rule ─────────────────────────────────────────────────────────────────

    /// Uma regra completa IF condition THEN action
    public class AutomationRule
    {
        public string Id          { get; set; } = Guid.NewGuid().ToString()[..8];
        public string Name        { get; set; } = "Unnamed Rule";
        public bool   IsEnabled   { get; set; } = true;

        public RuleCondition Condition { get; set; }
        public RuleAction    Action    { get; set; }

        /// Mínimo de ticks entre disparos consecutivos (evita spam)
        public int CooldownTicks  { get; set; } = 40;  // ~10s a 1×

        // ── Estado de runtime ─────────────────────────────────────────────
        public int  LastFiredTick  { get; set; } = -1;
        public int  FireCount      { get; set; } = 0;
        public bool WasActiveLastTick { get; set; } = false;

        public bool IsOnCooldown(int currentTick) =>
            LastFiredTick >= 0 && (currentTick - LastFiredTick) < CooldownTicks;

        public override string ToString() =>
            $"[{(IsEnabled ? "ON" : "OFF")}] IF {Condition} THEN {Action} (fired={FireCount}×)";
    }

    // ── RuleFireEvent ────────────────────────────────────────────────────────

    public class RuleFireEvent
    {
        public AutomationRule Rule      { get; set; }
        public int            Tick      { get; set; }
        public float          MetricVal { get; set; }
        public string         Summary   { get; set; }
    }

    // =========================================================================
    // AutomationSystem
    // =========================================================================

    public class AutomationSystem
    {
        private readonly SimulationEngine _engine;

        public List<AutomationRule> Rules { get; } = new();

        /// Evento disparado quando uma regra actua — para a UI mostrar feedback
        public event Action<RuleFireEvent> OnRuleFired;

        // Avalia regras a cada N ticks (não a cada tick — performance)
        private const int EvalInterval = 4;

        public AutomationSystem(SimulationEngine engine)
        {
            _engine = engine;
        }

        // ── API pública ───────────────────────────────────────────────────

        public void AddRule(AutomationRule rule)
        {
            Rules.Add(rule);
            _engine.RaiseLog($"Regra adicionada: {rule.Name}", LogLevel.Info);
        }

        public void RemoveRule(string ruleId)
        {
            var r = Rules.FirstOrDefault(r => r.Id == ruleId);
            if (r != null)
            {
                Rules.Remove(r);
                _engine.RaiseLog($"Regra removida: {r.Name}", LogLevel.Info);
            }
        }

        public void EnableRule(string ruleId)  => SetEnabled(ruleId, true);
        public void DisableRule(string ruleId) => SetEnabled(ruleId, false);

        private void SetEnabled(string ruleId, bool enabled)
        {
            var r = Rules.FirstOrDefault(r => r.Id == ruleId);
            if (r == null) return;
            r.IsEnabled = enabled;
            _engine.RaiseLog($"Regra '{r.Name}' {(enabled ? "activada" : "desactivada")}", LogLevel.Info);
        }

        // ── Tick ──────────────────────────────────────────────────────────

        public void Process()
        {
            if (_engine.Tick % EvalInterval != 0) return;

            foreach (var rule in Rules.Where(r => r.IsEnabled))
                EvaluateRule(rule);
        }

        // ── Avaliação ─────────────────────────────────────────────────────

        private void EvaluateRule(AutomationRule rule)
        {
            if (rule.IsOnCooldown(_engine.Tick)) return;

            float metricValue = ReadMetric(rule.Condition);
            bool  conditionMet = Compare(metricValue, rule.Condition.Comparator, rule.Condition.Threshold);

            // Edge detection: só dispara na transição OFF→ON (evita acção contínua)
            bool fire = conditionMet && !rule.WasActiveLastTick;

            // Excepção: acções de alerta puro disparam sempre que condição é verdadeira
            // (com cooldown a controlar frequência)
            if (rule.Action.ActionType == ActionType.Alert ||
                rule.Action.ActionType == ActionType.AlertAndStop)
            {
                fire = conditionMet;
            }

            rule.WasActiveLastTick = conditionMet;

            if (!fire) return;

            ExecuteAction(rule, metricValue);
        }

        // ── Leitura de métricas ───────────────────────────────────────────

        private float ReadMetric(RuleCondition cond)
        {
            Machine machine = cond.MachineId != null
                ? _engine.Machines.FirstOrDefault(m => m.Id == cond.MachineId)
                : null;

            Operator op = cond.OperatorId != null
                ? _engine.Operators.FirstOrDefault(o => o.Id == cond.OperatorId)
                : null;

            return cond.Metric switch
            {
                ConditionMetric.ToolWear =>
                    machine?.ToolWear ?? 0f,

                ConditionMetric.BufferOutFill =>
                    machine != null
                        ? (float)machine.BufferOut.Count / machine.BufferMax * 100f
                        : 0f,

                ConditionMetric.BufferInFill =>
                    machine != null
                        ? (float)machine.BufferIn.Count / machine.BufferMax * 100f
                        : 0f,

                ConditionMetric.RawBufferFill =>
                    (float)_engine.RawBuffer.Count / _engine.Config.RawBufferMax * 100f,

                ConditionMetric.ScrapRateShift =>
                    machine?.ScrapRateThisShift ?? 0f,

                ConditionMetric.ScrapRateGlobal =>
                    _engine.OEE > 0f ? 100f - _engine.OEE : 0f,

                ConditionMetric.OperatorFatigue =>
                    op?.Fatigue
                    ?? (machine?.AssignedOperator?.Fatigue ?? 0f),

                ConditionMetric.CycleTimeDeviation =>
                    machine != null && machine.CycleTimeBase > 0f
                        ? Math.Abs(machine.CycleTimeActual - machine.CycleTimeBase)
                          / machine.CycleTimeBase * 100f
                        : 0f,

                ConditionMetric.DeadlineTicksLeft =>
                    GetUrgentDeadlineTicksLeft(),

                ConditionMetric.ThroughputPerHour =>
                    _engine.ShiftTime > 0f
                        ? _engine.TotalPartsCompleted / (_engine.ShiftTime / 3600f)
                        : 0f,

                ConditionMetric.MachineState =>
                    machine != null ? (float)machine.State : -1f,

                _ => 0f
            };
        }

        private float GetUrgentDeadlineTicksLeft()
        {
            float min = float.MaxValue;
            foreach (var c in _engine.Contracts.Where(c => c.Status == ContractStatus.Active))
                foreach (var ms in c.Milestones.Where(m => !m.IsFulfilled && !m.IsFailed))
                {
                    float left = ms.DeadlineTick - _engine.Tick;
                    if (left < min) min = left;
                }
            return min == float.MaxValue ? float.MaxValue : min;
        }

        // ── Comparação ────────────────────────────────────────────────────

        private static bool Compare(float value, Comparator op, float threshold) => op switch
        {
            Comparator.GreaterThan    => value >  threshold,
            Comparator.GreaterOrEqual => value >= threshold,
            Comparator.LessThan       => value <  threshold,
            Comparator.LessOrEqual    => value <= threshold,
            Comparator.Equal          => Math.Abs(value - threshold) < 0.01f,
            Comparator.NotEqual       => Math.Abs(value - threshold) >= 0.01f,
            _                         => false,
        };

        // ── Execução de acções ────────────────────────────────────────────

        private void ExecuteAction(AutomationRule rule, float metricValue)
        {
            rule.LastFiredTick = _engine.Tick;
            rule.FireCount++;

            // Resolve máquina alvo: TargetMachineId > condição MachineId > null
            string targetId = rule.Action.TargetMachineId ?? rule.Condition.MachineId;
            Machine target  = targetId != null
                ? _engine.Machines.FirstOrDefault(m => m.Id == targetId)
                : null;

            switch (rule.Action.ActionType)
            {
                case ActionType.ChangeTool:
                    if (target != null)
                    {
                        _engine.ChangeTool(target);
                        Log($"AUTO [{rule.Name}]: ferramenta trocada em {target.Id} " +
                            $"(wear era {metricValue:F1}%)", LogLevel.Ok);
                    }
                    break;

                case ActionType.PauseMachine:
                    if (target != null)
                    {
                        target.State = MachineState.Paused;
                        Log($"AUTO [{rule.Name}]: {target.Id} pausada " +
                            $"(métrica={metricValue:F1})", LogLevel.Warn);
                    }
                    break;

                case ActionType.ResumeMachine:
                    if (target != null && target.State == MachineState.Paused)
                    {
                        target.State = MachineState.Idle;
                        Log($"AUTO [{rule.Name}]: {target.Id} retomada", LogLevel.Ok);
                    }
                    break;

                case ActionType.SlowCycle:
                    if (target != null)
                    {
                        float newCycle = target.CycleTimeBase / rule.Action.CycleFactor;
                        target.CycleTimeActual = newCycle;
                        Log($"AUTO [{rule.Name}]: {target.Id} ciclo abrandado " +
                            $"{target.CycleTimeBase:F1}s → {newCycle:F1}s " +
                            $"(factor={rule.Action.CycleFactor})", LogLevel.Warn);
                    }
                    break;

                case ActionType.RestoreCycle:
                    if (target != null)
                    {
                        target.CycleTimeActual = target.CycleTimeBase;
                        Log($"AUTO [{rule.Name}]: {target.Id} ciclo restaurado para {target.CycleTimeBase:F1}s", LogLevel.Ok);
                    }
                    break;

                case ActionType.SetCycleTimeFactor:
                    if (target != null)
                    {
                        target.CycleTimeActual = target.CycleTimeBase * rule.Action.CycleFactor;
                        Log($"AUTO [{rule.Name}]: {target.Id} ciclo = base × {rule.Action.CycleFactor} " +
                            $"= {target.CycleTimeActual:F1}s", LogLevel.Info);
                    }
                    break;

                case ActionType.StopLine:
                    _engine.EmergencyStop();
                    Log($"AUTO [{rule.Name}]: LINHA PARADA — {rule.Action.AlertMessage}", LogLevel.Danger);
                    break;

                case ActionType.Alert:
                    string alertMsg = string.IsNullOrEmpty(rule.Action.AlertMessage)
                        ? $"[{rule.Name}] condição activa: {rule.Condition} = {metricValue:F1}"
                        : $"[{rule.Name}] {rule.Action.AlertMessage} (valor={metricValue:F1})";
                    Log($"AUTO ALERT: {alertMsg}", LogLevel.Warn);
                    break;

                case ActionType.AlertAndStop:
                    Log($"AUTO [{rule.Name}]: ALERTA + PARAGEM — {rule.Action.AlertMessage} " +
                        $"(valor={metricValue:F1})", LogLevel.Danger);
                    _engine.EmergencyStop();
                    break;

                case ActionType.ChangeToolAndAlert:
                    if (target != null)
                    {
                        _engine.ChangeTool(target);
                        Log($"AUTO [{rule.Name}]: ferramenta trocada em {target.Id} + alerta " +
                            $"— {rule.Action.AlertMessage}", LogLevel.Warn);
                    }
                    break;
            }

            // Propaga evento para a UI
            OnRuleFired?.Invoke(new RuleFireEvent
            {
                Rule      = rule,
                Tick      = _engine.Tick,
                MetricVal = metricValue,
                Summary   = $"[{rule.Name}] disparou: {rule.Condition} = {metricValue:F1} → {rule.Action.ActionType}",
            });
        }

        private void Log(string msg, LogLevel level) =>
            _engine.RaiseLog(msg, level);

        // ── Factory de regras pré-definidas ───────────────────────────────
        //
        // Jogador começa com estas no early game.
        // Pode desligar, modificar threshold, ou criar novas.

        public static List<AutomationRule> CreateDefaultRules(List<Machine> machines)
        {
            var rules = new List<AutomationRule>();

            foreach (var m in machines)
            {
                // Troca de ferramenta automática a 80% wear
                rules.Add(new AutomationRule
                {
                    Name         = $"Auto Tool Change — {m.Id}",
                    IsEnabled    = true,
                    CooldownTicks= 1,  // só dispara uma vez (edge detection)
                    Condition    = new RuleCondition
                    {
                        Metric     = ConditionMetric.ToolWear,
                        MachineId  = m.Id,
                        Comparator = Comparator.GreaterOrEqual,
                        Threshold  = 80f,
                    },
                    Action = new RuleAction
                    {
                        ActionType     = ActionType.ChangeToolAndAlert,
                        TargetMachineId= m.Id,
                        AlertMessage   = $"wear ≥80% em {m.Id}",
                    },
                });

                // Abranda máquina quando buffer out >85%
                rules.Add(new AutomationRule
                {
                    Name         = $"Buffer Pressure — {m.Id}",
                    IsEnabled    = true,
                    CooldownTicks= 20,
                    Condition    = new RuleCondition
                    {
                        Metric     = ConditionMetric.BufferOutFill,
                        MachineId  = m.Id,
                        Comparator = Comparator.GreaterThan,
                        Threshold  = 85f,
                    },
                    Action = new RuleAction
                    {
                        ActionType     = ActionType.SlowCycle,
                        TargetMachineId= m.Id,
                        CycleFactor    = 0.65f,
                        AlertMessage   = $"buffer out cheio em {m.Id}",
                    },
                });

                // Restaura ciclo quando buffer out <40%
                rules.Add(new AutomationRule
                {
                    Name         = $"Buffer Clear — {m.Id}",
                    IsEnabled    = true,
                    CooldownTicks= 20,
                    Condition    = new RuleCondition
                    {
                        Metric     = ConditionMetric.BufferOutFill,
                        MachineId  = m.Id,
                        Comparator = Comparator.LessThan,
                        Threshold  = 40f,
                    },
                    Action = new RuleAction
                    {
                        ActionType     = ActionType.RestoreCycle,
                        TargetMachineId= m.Id,
                    },
                });
            }

            // Alerta global de scrap rate
            rules.Add(new AutomationRule
            {
                Name         = "Global Scrap Alert",
                IsEnabled    = true,
                CooldownTicks= 160,  // ~40s a 1×
                Condition    = new RuleCondition
                {
                    Metric     = ConditionMetric.ScrapRateGlobal,
                    Comparator = Comparator.GreaterThan,
                    Threshold  = 15f,
                },
                Action = new RuleAction
                {
                    ActionType   = ActionType.Alert,
                    AlertMessage = "Scrap global >15% — investigar causa",
                },
            });

            // Pausa linha se raw buffer crítico
            rules.Add(new AutomationRule
            {
                Name         = "Raw Buffer Critical",
                IsEnabled    = true,
                CooldownTicks= 80,
                Condition    = new RuleCondition
                {
                    Metric     = ConditionMetric.RawBufferFill,
                    Comparator = Comparator.LessThan,
                    Threshold  = 5f,
                },
                Action = new RuleAction
                {
                    ActionType   = ActionType.Alert,
                    AlertMessage = "Raw buffer <5% — reabastecer MP urgente",
                },
            });

            // Alerta deadline urgente
            rules.Add(new AutomationRule
            {
                Name         = "Deadline Warning",
                IsEnabled    = true,
                CooldownTicks= 240,
                Condition    = new RuleCondition
                {
                    Metric     = ConditionMetric.DeadlineTicksLeft,
                    Comparator = Comparator.LessThan,
                    Threshold  = 480f,   // ~2h simuladas a 1×
                },
                Action = new RuleAction
                {
                    ActionType   = ActionType.Alert,
                    AlertMessage = "Deadline em <2h — verificar projecção de entrega",
                },
            });

            return rules;
        }
    }
}
