using System;
using System.Collections.Generic;
using IndustrialChaos.Core.Models;

namespace IndustrialChaos.Core.Systems
{
    /// <summary>
    /// Gere o estado dinâmico dos operadores:
    /// fadiga, eventos de personalidade, mentoria, ausências.
    /// Corre uma vez por turno (não por tick — eventos humanos têm outra cadência).
    /// </summary>
    public class OperatorSystem
    {
        private readonly SimulationEngine _engine;
        private readonly Random           _rng;

        // Cadência de eventos: a cada quantos ticks avalia eventos de personalidade
        private const int PersonalityEvalInterval = 240; // ~1 hora simulada a 1×

        public OperatorSystem(SimulationEngine engine, int? seed = null)
        {
            _engine = engine;
            _rng    = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        // ── Tick ──────────────────────────────────────────────────────────

        public void Process(float deltaTime)
        {
            float hoursThisTick = deltaTime / 3600f;

            foreach (var op in _engine.Operators)
            {
                if (!op.IsPresent) continue;

                UpdateFatigue(op, hoursThisTick);
                ApplyMentoring(op);
            }

            // Eventos de personalidade numa cadência mais lenta
            if (_engine.Tick % PersonalityEvalInterval == 0)
                EvaluatePersonalityEvents();
        }

        // ── Fadiga ────────────────────────────────────────────────────────

        private void UpdateFatigue(Operator op, float hoursThisTick)
        {
            if (!op.IsAssigned) return;

            float rate = op.FatigueRate * (1f - op.Personality.FatigueResistance);
            op.Fatigue = Math.Min(100f, op.Fatigue + rate * hoursThisTick);
            op.HoursWorkedThisShift += hoursThisTick;

            if (op.Fatigue > 70f && op.Fatigue - rate * hoursThisTick <= 70f)
            {
                // Acaba de cruzar o threshold
                var ev = new OperatorEvent
                {
                    Type    = OperatorEventType.FatigueWarning,
                    Message = $"{op.Name}: fadiga >70% — performance a degradar",
                    Tick    = _engine.Tick
                };
                _engine.RaiseOperatorEvent(op, ev);
                _engine.RaiseLog(ev.Message, LogLevel.Warn);
            }
        }

        public void RecoverFatigue(Operator op, float hoursRest)
        {
            op.Fatigue = Math.Max(0f, op.Fatigue - op.FatigueRecoveryRate * hoursRest);
        }

        // ── Mentoria ─────────────────────────────────────────────────────

        private void ApplyMentoring(Operator mentor)
        {
            if (!mentor.IsMentor || mentor.AssignedMachine == null) return;

            // Procura júniores na mesma máquina ou adjacente
            foreach (var junior in _engine.Operators)
            {
                if (junior == mentor) continue;
                if (junior.Age >= 45) continue;  // não é júnior
                if (!IsNearby(mentor, junior)) continue;

                // XP bónus por mentoria
                if (mentor.AssignedMachine?.OpType is OperationType opType)
                {
                    float current = junior.Skills.GetValueOrDefault(opType, 20f);
                    junior.Skills[opType] = Math.Min(100f, current + 0.005f); // pequeno mas consistente

                    if (_engine.Tick % 480 == 0) // log a cada ~2h simuladas
                    {
                        var ev = new OperatorEvent
                        {
                            Type    = OperatorEventType.Mentoring,
                            Message = $"{mentor.Name} ({mentor.Age}a) a ensinar {junior.Name} em {opType}",
                            Tick    = _engine.Tick
                        };
                        _engine.RaiseOperatorEvent(mentor, ev);
                        _engine.RaiseLog(ev.Message, LogLevel.Info);
                    }
                }
            }
        }

        private bool IsNearby(Operator a, Operator b) =>
            a.AssignedMachine != null && b.AssignedMachine != null &&
            a.AssignedMachine.OpType == b.AssignedMachine.OpType;

        // ── Eventos de personalidade ──────────────────────────────────────

        private void EvaluatePersonalityEvents()
        {
            foreach (var op in _engine.Operators)
                EvaluateSingleOperator(op);
        }

        private void EvaluateSingleOperator(Operator op)
        {
            var p = op.Personality;

            // ── Ausência ──────────────────────────────────────────────────
            if (op.IsPresent && Roll(p.AbsenceChance * 0.08f))
            {
                op.IsPresent = false;
                if (op.AssignedMachine != null)
                {
                    op.AssignedMachine.AssignedOperator = null;
                    op.AssignedMachine = null;
                }
                var ev = new OperatorEvent
                {
                    Type    = OperatorEventType.Absent,
                    Message = $"{op.Name} não compareceu — razão desconhecida",
                    Tick    = _engine.Tick
                };
                _engine.RaiseOperatorEvent(op, ev);
                _engine.RaiseLog(ev.Message, LogLevel.Danger);
                return;
            }

            // ── Regresso após ausência ────────────────────────────────────
            if (!op.IsPresent && Roll(0.7f))
            {
                op.IsPresent = true;
                _engine.RaiseLog($"{op.Name} regressou ao trabalho", LogLevel.Ok);
            }

            if (!op.IsPresent) return;

            // ── Penalidade pós-evento social (futebol, saída) ─────────────
            if (p.HasSocialHabits && Roll(0.15f))
            {
                // Simula efeito "dia a seguir" — skill efectiva reduzida temporariamente
                // Implementado como fadiga extra adicionada
                op.Fatigue = Math.Min(100f, op.Fatigue + 25f);
                var ev = new OperatorEvent
                {
                    Type    = OperatorEventType.ProductivityDrop,
                    Message = $"{op.Name}: rendimento baixo — possível saída na véspera",
                    Tick    = _engine.Tick
                };
                _engine.RaiseOperatorEvent(op, ev);
                _engine.RaiseLog(ev.Message, LogLevel.Warn);
            }

            // ── Overtime espontâneo (dedicação + dívidas) ─────────────────
            if (op.IsAssigned && Roll(p.OvertimeChance * 0.12f))
            {
                // Produz mais mas acumula fadiga
                op.Fatigue = Math.Min(100f, op.Fatigue + 10f);
                var ev = new OperatorEvent
                {
                    Type    = OperatorEventType.Overtime,
                    Message = $"{op.Name}: ficou em horas extra — throughput +15% neste turno",
                    Tick    = _engine.Tick
                };
                _engine.RaiseOperatorEvent(op, ev);
                _engine.RaiseLog(ev.Message, LogLevel.Info);
            }

            // ── Veterano previne avaria ───────────────────────────────────
            if (op.IsMentor && op.AssignedMachine != null)
            {
                var m = op.AssignedMachine;
                if (m.ToolWear > 65f && Roll(0.3f))
                {
                    // Experiência deteta desgaste antes da regra automática
                    var ev = new OperatorEvent
                    {
                        Type    = OperatorEventType.FaultPrevented,
                        Message = $"{op.Name} ({op.Age}a): detectou desgaste em {m.Id} — recomenda troca",
                        Tick    = _engine.Tick
                    };
                    _engine.RaiseOperatorEvent(op, ev);
                    _engine.RaiseLog(ev.Message, LogLevel.Warn);
                    // Não age sozinho — avisa o jogador. Decisão é do jogador.
                }
            }

            // ── Stress sob deadline apertado ──────────────────────────────
            bool deadlineStress = _engine.HasUrgentDeadline();
            if (deadlineStress && op.Personality.StressTolerance < 0.3f && Roll(0.2f))
            {
                op.Fatigue = Math.Min(100f, op.Fatigue + 15f);
                _engine.RaiseLog($"{op.Name}: stress com deadline — fadiga +15%", LogLevel.Warn);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// Roll probabilístico: retorna true com probabilidade p (0–1)
        private bool Roll(float probability) =>
            (float)_rng.NextDouble() < Math.Clamp(probability, 0f, 1f);

        /// Força ausência (para testes ou eventos de crise)
        public void ForceAbsence(Operator op)
        {
            op.IsPresent = false;
            if (op.AssignedMachine != null)
            {
                op.AssignedMachine.AssignedOperator = null;
                op.AssignedMachine = null;
            }
        }

        /// Recuperação completa de fadiga (fim de turno / descanso)
        public void EndShift(Operator op)
        {
            RecoverFatigue(op, 8f); // 8h de descanso
            op.ResetShiftCounters();
        }
    }
}
