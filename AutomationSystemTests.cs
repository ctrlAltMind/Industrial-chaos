// ============================================================
// INDUSTRIAL CHAOS — Testes do AutomationSystem
// ============================================================
// Ficheiro: Tests/EditMode/AutomationSystemTests.cs
// ============================================================

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using IndustrialChaos.Core;
using IndustrialChaos.Core.Models;
using IndustrialChaos.Core.Systems;

namespace IndustrialChaos.Tests
{
    [TestFixture]
    public class AutomationSystemTests
    {
        // ── Engine mínimo para testar automação ───────────────────────────

        private static (SimulationEngine engine, AutomationSystem automation) BuildTestEngine()
        {
            var config = new SimulationConfig();

            var machines = new List<Machine>
            {
                new Machine { Id="CNC-01",  OpType=OperationType.CNC,  BaseScrapPct=3f, WearPerPart=0.18f, CycleTimeBase=4.5f, CycleTimeActual=4.5f, EnergyKw=2.4f, BufferMax=20 },
                new Machine { Id="WELD-01", OpType=OperationType.Weld, BaseScrapPct=5f, WearPerPart=0.22f, CycleTimeBase=6.5f, CycleTimeActual=6.5f, EnergyKw=4.1f, BufferMax=15 },
                new Machine { Id="INSP-01", OpType=OperationType.Insp, BaseScrapPct=1f, WearPerPart=0.08f, CycleTimeBase=3.0f, CycleTimeActual=3.0f, EnergyKw=0.8f, BufferMax=20 },
            };

            var op = new Operator { Id="OP-1", Name="Ferreira", Age=42 };
            op.Skills[OperationType.CNC] = 82f;

            var milestone = new DeliveryMilestone { QuantityRequired=50, DeadlineTick=99999, PenaltyPerMissed=8f };
            var contract  = new Contract
            {
                Name="C-001", PartTypeId="BRACKET-A7", TotalQuantity=50,
                QualityReq=QualityRequirement.AandB,
                Milestones = new List<DeliveryMilestone> { milestone },
                Status=ContractStatus.Active,
                PricePerPartA=21f, PricePerPartB=14f,
            };

            var batch = new MaterialBatch
            {
                SupplierId="SUP-1", PartTypeId="BRACKET-A7",
                Quality=1f, TotalParts=500, Remaining=500,
            };

            var engine = new SimulationEngine();
            engine.Setup(config, machines, new List<Operator>{op},
                         new List<Contract>{contract}, batch, seed:42);
            engine.AssignOperator(op, machines[0]);

            var automation = new AutomationSystem(engine);
            return (engine, automation);
        }

        private static AutomationRule MakeRule(
            ConditionMetric metric,
            Comparator      comparator,
            float           threshold,
            ActionType      action,
            string          machineId     = "CNC-01",
            string          targetId      = null,
            float           cycleFactor   = 0.7f,
            string          alertMsg      = "",
            int             cooldown      = 1)
        {
            return new AutomationRule
            {
                Name          = $"TEST_{metric}_{action}",
                IsEnabled     = true,
                CooldownTicks = cooldown,
                Condition     = new RuleCondition
                {
                    Metric     = metric,
                    MachineId  = machineId,
                    Comparator = comparator,
                    Threshold  = threshold,
                },
                Action = new RuleAction
                {
                    ActionType      = action,
                    TargetMachineId = targetId ?? machineId,
                    CycleFactor     = cycleFactor,
                    AlertMessage    = alertMsg,
                },
            };
        }

        // ─────────────────────────────────────────────────────────────────
        // 1. SETUP E REGRAS PADRÃO
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void DefaultRules_AreCreated_ForAllMachines()
        {
            var (engine, _) = BuildTestEngine();
            var rules = AutomationSystem.CreateDefaultRules(engine.Machines);

            // Cada máquina gera 3 regras (tool change, buffer pressure, buffer clear)
            // + 3 regras globais = 3×3 + 3 = 12
            Assert.AreEqual(engine.Machines.Count * 3 + 3, rules.Count,
                $"Esperado {engine.Machines.Count * 3 + 3} regras, obtido {rules.Count}");
        }

        [Test]
        public void DefaultRules_AllEnabled_ByDefault()
        {
            var (engine, _) = BuildTestEngine();
            var rules = AutomationSystem.CreateDefaultRules(engine.Machines);

            Assert.IsTrue(rules.All(r => r.IsEnabled),
                "Todas as regras padrão devem estar activadas");
        }

        [Test]
        public void AddRule_IncreasesRuleCount()
        {
            var (engine, auto) = BuildTestEngine();
            int before = auto.Rules.Count;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 50f, ActionType.Alert));

            Assert.AreEqual(before + 1, auto.Rules.Count);
        }

        [Test]
        public void RemoveRule_DecreasesRuleCount()
        {
            var (engine, auto) = BuildTestEngine();
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 50f, ActionType.Alert);
            auto.AddRule(rule);
            int before = auto.Rules.Count;

            auto.RemoveRule(rule.Id);

            Assert.AreEqual(before - 1, auto.Rules.Count);
        }

        [Test]
        public void EnableDisableRule_TogglesIsEnabled()
        {
            var (engine, auto) = BuildTestEngine();
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 50f, ActionType.Alert);
            auto.AddRule(rule);

            auto.DisableRule(rule.Id);
            Assert.IsFalse(rule.IsEnabled);

            auto.EnableRule(rule.Id);
            Assert.IsTrue(rule.IsEnabled);
        }

        // ─────────────────────────────────────────────────────────────────
        // 2. CONDIÇÕES — LEITURA DE MÉTRICAS
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Condition_ToolWear_FiresWhenAboveThreshold()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0]; // CNC-01
            machine.ToolWear = 85f;

            bool fired = false;
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f, ActionType.Alert,
                                alertMsg: "wear test", cooldown: 1);
            rule.Action.ActionType = ActionType.Alert;
            auto.AddRule(rule);
            auto.OnRuleFired += _ => fired = true;

            // Process avalia a cada EvalInterval=4 ticks
            for (int i = 0; i < 5; i++) auto.Process();

            Assert.IsTrue(fired, "Regra deve disparar com ToolWear 85 > threshold 80");
        }

        [Test]
        public void Condition_ToolWear_DoesNotFire_WhenBelowThreshold()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 50f;

            bool fired = false;
            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f, ActionType.Alert));
            auto.OnRuleFired += _ => fired = true;

            for (int i = 0; i < 20; i++) auto.Process();

            Assert.IsFalse(fired, "Regra não deve disparar com ToolWear 50 < threshold 80");
        }

        [Test]
        public void Condition_BufferOutFill_FiresCorrectly()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];

            // Enche buffer out acima do threshold
            for (int i = 0; i < 18; i++)
                machine.BufferOut.Enqueue(new Part { PartTypeId = "BRACKET-A7" });
            // 18/20 = 90% > 85%

            bool fired = false;
            auto.AddRule(MakeRule(ConditionMetric.BufferOutFill, Comparator.GreaterThan, 85f,
                                  ActionType.Alert, cooldown:1));
            auto.OnRuleFired += _ => fired = true;

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.IsTrue(fired, "Regra deve disparar com buffer 90% > 85%");
        }

        [Test]
        public void Condition_RawBufferFill_UsesGlobalBuffer()
        {
            var (engine, auto) = BuildTestEngine();

            // Esvazia o raw buffer quase todo
            while (engine.RawBuffer.Count > 3)
                engine.RawBuffer.Dequeue();
            // <5% do max (100)

            bool fired = false;
            auto.AddRule(new AutomationRule
            {
                Name         = "Raw Low",
                IsEnabled    = true,
                CooldownTicks= 1,
                Condition    = new RuleCondition
                {
                    Metric     = ConditionMetric.RawBufferFill,
                    Comparator = Comparator.LessThan,
                    Threshold  = 5f,
                    MachineId  = null,  // global
                },
                Action = new RuleAction { ActionType = ActionType.Alert, AlertMessage = "raw low" },
            });
            auto.OnRuleFired += _ => fired = true;

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.IsTrue(fired, "Regra global deve disparar com raw buffer <5%");
        }

        [Test]
        public void Condition_OperatorFatigue_ReadFromAssignedMachine()
        {
            var (engine, auto) = BuildTestEngine();
            var op = engine.Operators[0];
            op.Fatigue = 80f;
            // op está em CNC-01

            bool fired = false;
            auto.AddRule(MakeRule(ConditionMetric.OperatorFatigue, Comparator.GreaterThan, 70f,
                                  ActionType.Alert, machineId:"CNC-01", cooldown:1));
            auto.OnRuleFired += _ => fired = true;

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.IsTrue(fired, "Condição de fadiga deve ler do operador alocado à máquina");
        }

        // ─────────────────────────────────────────────────────────────────
        // 3. COMPARADORES
        // ─────────────────────────────────────────────────────────────────

        [TestCase(Comparator.GreaterThan,    85f, 80f, true)]
        [TestCase(Comparator.GreaterThan,    80f, 80f, false)]
        [TestCase(Comparator.GreaterOrEqual, 80f, 80f, true)]
        [TestCase(Comparator.LessThan,       50f, 80f, true)]
        [TestCase(Comparator.LessThan,       80f, 80f, false)]
        [TestCase(Comparator.LessOrEqual,    80f, 80f, true)]
        [TestCase(Comparator.Equal,          80f, 80f, true)]
        [TestCase(Comparator.Equal,          81f, 80f, false)]
        [TestCase(Comparator.NotEqual,       81f, 80f, true)]
        [TestCase(Comparator.NotEqual,       80f, 80f, false)]
        public void Comparator_AllVariants_WorkCorrectly(
            Comparator comp, float wear, float threshold, bool shouldFire)
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = wear;

            bool fired = false;
            auto.AddRule(MakeRule(ConditionMetric.ToolWear, comp, threshold,
                                  ActionType.Alert, cooldown:1));
            auto.OnRuleFired += _ => fired = true;

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.AreEqual(shouldFire, fired,
                $"ToolWear={wear} {comp} threshold={threshold} → esperado fired={shouldFire}");
        }

        // ─────────────────────────────────────────────────────────────────
        // 4. ACÇÕES
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Action_ChangeTool_ResetsWearToZero()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];
            machine.ToolWear = 85f;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                  ActionType.ChangeTool, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.AreEqual(0f, machine.ToolWear,
                "ChangeTool deve resetar ToolWear para 0");
        }

        [Test]
        public void Action_ChangeTool_AddsCostToEngine()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];
            machine.ToolWear = 85f;
            float costBefore = engine.ToolCost;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                  ActionType.ChangeTool, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.Greater(engine.ToolCost, costBefore,
                "ChangeTool automático deve adicionar custo ao engine");
        }

        [Test]
        public void Action_PauseMachine_SetsMachinePaused()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];
            machine.ToolWear = 95f;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 90f,
                                  ActionType.PauseMachine, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.AreEqual(MachineState.Paused, machine.State,
                "PauseMachine deve colocar máquina em Paused");
        }

        [Test]
        public void Action_ResumeMachine_SetsIdle_IfWasPaused()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];
            machine.State    = MachineState.Paused;
            machine.ToolWear = 10f;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.LessThan, 80f,
                                  ActionType.ResumeMachine, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.AreNotEqual(MachineState.Paused, machine.State,
                "ResumeMachine deve tirar máquina do estado Paused");
        }

        [Test]
        public void Action_SlowCycle_IncreasesActualCycleTime()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0]; // CycleTimeBase = 4.5
            float baseCycle = machine.CycleTimeBase;

            // Enche buffer para trigger
            for (int i = 0; i < 18; i++)
                machine.BufferOut.Enqueue(new Part { PartTypeId = "BRACKET-A7" });

            auto.AddRule(MakeRule(ConditionMetric.BufferOutFill, Comparator.GreaterThan, 85f,
                                  ActionType.SlowCycle, cycleFactor:0.65f, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            float expected = baseCycle / 0.65f;
            Assert.AreEqual(expected, machine.CycleTimeActual, 0.01f,
                $"SlowCycle(0.65) deve dar CycleTimeActual={expected:F2}");
        }

        [Test]
        public void Action_RestoreCycle_ResetsToCycleTimeBase()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];
            machine.CycleTimeActual = 99f; // artificialmente alterado
            machine.ToolWear = 10f;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.LessThan, 80f,
                                  ActionType.RestoreCycle, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.AreEqual(machine.CycleTimeBase, machine.CycleTimeActual, 0.001f,
                "RestoreCycle deve repor CycleTimeActual = CycleTimeBase");
        }

        [Test]
        public void Action_StopLine_PausesAllMachines()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 99f;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 95f,
                                  ActionType.StopLine, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.IsTrue(engine.Machines.All(m => m.State == MachineState.Paused),
                "StopLine deve pausar todas as máquinas");
        }

        [Test]
        public void Action_Alert_AddsEntryToLog()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 85f;
            int logBefore = engine.Log.Count;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                  ActionType.Alert, alertMsg:"teste alerta", cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            Assert.Greater(engine.Log.Count, logBefore,
                "Alert deve adicionar entrada ao log do engine");
        }

        [Test]
        public void Action_SetCycleTimeFactor_CalculatesCorrectly()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0]; // base = 4.5s
            machine.ToolWear = 85f;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                  ActionType.SetCycleTimeFactor, cycleFactor:1.3f, cooldown:1));

            for (int i = 0; i < 5; i++) auto.Process();

            float expected = machine.CycleTimeBase * 1.3f;
            Assert.AreEqual(expected, machine.CycleTimeActual, 0.01f,
                $"SetCycleTimeFactor(1.3) deve dar {expected:F2}s");
        }

        // ─────────────────────────────────────────────────────────────────
        // 5. COOLDOWN E EDGE DETECTION
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Rule_DoesNotFire_WhileOnCooldown()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 85f;

            int fireCount = 0;
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                ActionType.Alert, cooldown:200);  // cooldown grande
            auto.AddRule(rule);
            auto.OnRuleFired += _ => fireCount++;

            // Simula muitos ticks — cooldown impede múltiplos disparos
            for (int i = 0; i < 100; i++)
            {
                // Força tick para ser múltiplo do eval interval
                IncrementTick(engine);
                auto.Process();
            }

            Assert.LessOrEqual(fireCount, 1,
                "Com cooldown alto, regra não deve disparar mais de 1 vez");
        }

        [Test]
        public void Rule_FiresAgain_AfterCooldownExpires()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 85f;

            int fireCount = 0;
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                ActionType.Alert, cooldown:4);  // cooldown curto
            auto.AddRule(rule);
            auto.OnRuleFired += _ => fireCount++;

            // Simula ticks suficientes para cooldown expirar várias vezes
            for (int i = 0; i < 60; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            // Alert usa edge=false (dispara sempre que condição está activa + cooldown ok)
            Assert.Greater(fireCount, 1,
                "Alert deve poder disparar múltiplas vezes após cooldown expirar");
        }

        [Test]
        public void Rule_EdgeDetection_ChangeTool_FiresOnce_ThenNotAgain()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];
            machine.ToolWear = 85f;

            int fireCount = 0;
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                ActionType.ChangeTool, cooldown:1);
            auto.AddRule(rule);
            auto.OnRuleFired += _ => fireCount++;

            for (int i = 0; i < 20; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            // ChangeTool usa edge detection: dispara 1× na transição OFF→ON
            // Depois de trocar ferramenta, wear = 0 → condição falsa → edge reset
            Assert.AreEqual(1, fireCount,
                "ChangeTool deve disparar exactamente 1× na transição (edge detection)");
        }

        [Test]
        public void Rule_Disabled_DoesNotFire()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 95f;

            bool fired = false;
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                ActionType.Alert, cooldown:1);
            rule.IsEnabled = false;
            auto.AddRule(rule);
            auto.OnRuleFired += _ => fired = true;

            for (int i = 0; i < 20; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            Assert.IsFalse(fired, "Regra desactivada não deve disparar");
        }

        // ─────────────────────────────────────────────────────────────────
        // 6. EVENTOS E CALLBACKS
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void OnRuleFired_Event_ContainsCorrectData()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 85f;

            RuleFireEvent received = null;
            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                ActionType.Alert, alertMsg:"wear alto", cooldown:1);
            auto.AddRule(rule);
            auto.OnRuleFired += e => received = e;

            for (int i = 0; i < 5; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            Assert.IsNotNull(received);
            Assert.AreEqual(rule, received.Rule);
            Assert.AreEqual(85f, received.MetricVal, 0.1f);
            Assert.IsNotNull(received.Summary);
        }

        [Test]
        public void Rule_FireCount_IncrementsOnEachFire()
        {
            var (engine, auto) = BuildTestEngine();
            engine.Machines[0].ToolWear = 85f;

            var rule = MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                ActionType.Alert, cooldown:4);
            auto.AddRule(rule);

            for (int i = 0; i < 40; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            Assert.Greater(rule.FireCount, 0, "FireCount deve incrementar com cada disparo");
        }

        // ─────────────────────────────────────────────────────────────────
        // 7. INTEGRAÇÃO COM ENGINE
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Integration_DefaultRules_WorkWithEngine_NoExceptions()
        {
            var (engine, auto) = BuildTestEngine();
            var defaultRules = AutomationSystem.CreateDefaultRules(engine.Machines);
            foreach (var r in defaultRules)
                auto.AddRule(r);

            // Simula 200 ticks completos sem excepção
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    engine.Step();
                    auto.Process();
                }
            }, "Engine + AutomationSystem não devem lançar excepções em execução normal");
        }

        [Test]
        public void Integration_ToolChangeRule_TriggersBeforeFault()
        {
            var (engine, auto) = BuildTestEngine();
            var machine = engine.Machines[0];

            // Regra: troca ferramenta a 85% (antes do fault threshold que é 98%)
            auto.AddRule(new AutomationRule
            {
                Name         = "Pre-Fault Tool Change",
                IsEnabled    = true,
                CooldownTicks= 1,
                Condition    = new RuleCondition
                {
                    Metric     = ConditionMetric.ToolWear,
                    MachineId  = "CNC-01",
                    Comparator = Comparator.GreaterOrEqual,
                    Threshold  = 85f,
                },
                Action = new RuleAction
                {
                    ActionType      = ActionType.ChangeTool,
                    TargetMachineId = "CNC-01",
                },
            });

            machine.ToolWear = 85f;

            for (int i = 0; i < 10; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            Assert.Less(machine.ToolWear, machine.FaultThreshold,
                "Tool change automático deve prevenir FAULT");
            Assert.AreNotEqual(MachineState.Fault, machine.State);
        }

        [Test]
        public void Integration_MultipleRules_ExecuteIndependently()
        {
            var (engine, auto) = BuildTestEngine();
            var cnc  = engine.Machines[0];
            var weld = engine.Machines[1];

            cnc.ToolWear  = 85f;
            weld.ToolWear = 90f;

            bool cncFired  = false;
            bool weldFired = false;

            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                  ActionType.Alert, machineId:"CNC-01", cooldown:1));
            auto.AddRule(MakeRule(ConditionMetric.ToolWear, Comparator.GreaterThan, 80f,
                                  ActionType.Alert, machineId:"WELD-01", cooldown:1));

            auto.OnRuleFired += e =>
            {
                if (e.Rule.Condition.MachineId == "CNC-01")  cncFired  = true;
                if (e.Rule.Condition.MachineId == "WELD-01") weldFired = true;
            };

            for (int i = 0; i < 5; i++)
            {
                IncrementTick(engine);
                auto.Process();
            }

            Assert.IsTrue(cncFired,  "Regra CNC-01 deve ter disparado");
            Assert.IsTrue(weldFired, "Regra WELD-01 deve ter disparado");
        }

        // ─────────────────────────────────────────────────────────────────
        // HELPER — forçar tick sem correr toda a simulação
        // ─────────────────────────────────────────────────────────────────

        /// Avança o tick do engine via reflexão para testes de cooldown
        /// sem precisar de correr toda a produção
        private static void IncrementTick(SimulationEngine engine)
        {
            // Em Unity usarias engine.Step() directamente.
            // Aqui acedemos via reflexão para isolar o AutomationSystem.
            var tickProp = typeof(SimulationEngine)
                .GetProperty("Tick", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (tickProp != null && tickProp.CanWrite)
            {
                tickProp.SetValue(engine, (int)tickProp.GetValue(engine) + 1);
            }
            else
            {
                // Fallback: usa Step() se Tick não for settable
                engine.Step();
            }
        }
    }
}
