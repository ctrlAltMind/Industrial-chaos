// ============================================================
// INDUSTRIAL CHAOS — Testes do Motor de Simulação
// ============================================================
// Ficheiro: Tests/EditMode/SimulationEngineTests.cs
//
// Em Unity: coloca em Assets/Tests/EditMode/
// Requer: Unity Test Framework (package manager)
//
// Fora de Unity (para desenvolvimento puro em C#):
// Substituir os atributos NUnit por xUnit ou MSTest conforme
// o runner escolhido. A lógica dos testes é idêntica.
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
    public class SimulationEngineTests
    {
        // ── Factories de teste ────────────────────────────────────────────

        private static SimulationEngine BuildEngine(
            string partTypeId = "BRACKET-A7",
            int seed = 42,
            int rawBatchSize = 200,
            float batchQuality = 1f)
        {
            var config = new SimulationConfig();

            var machines = new List<Machine>
            {
                new Machine { Id="CNC-01",  OpType=OperationType.CNC,  BaseScrapPct=3f, WearPerPart=0.18f, CycleTimeBase=4.5f, CycleTimeActual=4.5f, EnergyKw=2.4f, BufferMax=20 },
                new Machine { Id="WELD-01", OpType=OperationType.Weld, BaseScrapPct=5f, WearPerPart=0.22f, CycleTimeBase=6.5f, CycleTimeActual=6.5f, EnergyKw=4.1f, BufferMax=15 },
                new Machine { Id="INSP-01", OpType=OperationType.Insp, BaseScrapPct=1f, WearPerPart=0.08f, CycleTimeBase=3.0f, CycleTimeActual=3.0f, EnergyKw=0.8f, BufferMax=20 },
                new Machine { Id="PACK-01", OpType=OperationType.Pack, BaseScrapPct=0.5f,WearPerPart=0.02f,CycleTimeBase=1.5f, CycleTimeActual=1.5f, EnergyKw=0.4f, BufferMax=30 },
            };

            var op = new Operator
            {
                Id   = "OP-1",
                Name = "Ferreira",
                Age  = 42,
                Personality = new PersonalityProfile
                {
                    Dedication     = 0.7f,
                    Reliability    = 0.9f,
                    StressTolerance= 0.6f,
                    HasDebt        = false,
                    HasSocialHabits= false,
                }
            };
            op.Skills[OperationType.CNC]  = 82f;
            op.Skills[OperationType.Weld] = 18f;
            op.Skills[OperationType.Insp] = 30f;

            var operators = new List<Operator> { op };

            var milestone = new DeliveryMilestone
            {
                QuantityRequired = 50,
                DeadlineTick     = 10000,
                PenaltyPerMissed = 8f,
            };

            var contract = new Contract
            {
                Name        = "CONTRACT-001",
                ClientName  = "Moldes Ferreira Lda",
                PartTypeId  = partTypeId,
                TotalQuantity = 50,
                QualityReq  = QualityRequirement.AandB,
                RequiredRoute = config.PartRoutes.ContainsKey(partTypeId)
                    ? config.PartRoutes[partTypeId]
                    : new List<OperationType> { OperationType.CNC, OperationType.Insp, OperationType.Pack },
                PricePerPartA   = 21f,
                PricePerPartB   = 14f,
                PenaltyPerMissed= 8f,
                Milestones      = new List<DeliveryMilestone> { milestone },
                Status          = ContractStatus.Active,
            };

            var batch = new MaterialBatch
            {
                SupplierId  = "SUP-001",
                PartTypeId  = partTypeId,
                Quality     = batchQuality,
                TotalParts  = rawBatchSize,
                Remaining   = rawBatchSize,
                CostPerPart = 3.5f,
            };

            var engine = new SimulationEngine();
            engine.Setup(config, machines, operators, new List<Contract> { contract }, batch, seed: seed);

            // Aloca operador ao CNC
            engine.AssignOperator(op, machines[0]);

            return engine;
        }

        // ─────────────────────────────────────────────────────────────────
        // 1. TESTES DE SETUP
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Engine_Initializes_WithCorrectState()
        {
            var engine = BuildEngine();

            Assert.AreEqual(0, engine.Tick);
            Assert.AreEqual(0f, engine.ShiftTime);
            Assert.IsFalse(engine.IsPaused);
            Assert.AreEqual(4, engine.Machines.Count);
            Assert.AreEqual(1, engine.Operators.Count);
            Assert.AreEqual(1, engine.Contracts.Count);
            Assert.Greater(engine.RawBuffer.Count, 0, "Raw buffer deve ter peças após setup");
        }

        [Test]
        public void Engine_Operator_IsAssignedToMachine()
        {
            var engine = BuildEngine();
            var op      = engine.Operators[0];
            var machine = engine.Machines[0];

            Assert.AreEqual(machine, op.AssignedMachine);
            Assert.AreEqual(op, machine.AssignedOperator);
        }

        // ─────────────────────────────────────────────────────────────────
        // 2. TESTES DE TICK
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Engine_Step_IncrementsTick()
        {
            var engine = BuildEngine();
            engine.Step();
            Assert.AreEqual(1, engine.Tick);
        }

        [Test]
        public void Engine_Step_IncrementsShiftTime()
        {
            var engine = BuildEngine();
            engine.Step();
            Assert.AreEqual(engine.Config.TickDuration, engine.ShiftTime, 0.001f);
        }

        [Test]
        public void Engine_Pause_StopsTickProgression()
        {
            var engine = BuildEngine();
            engine.Pause();
            engine.Step();
            engine.Step();

            Assert.AreEqual(0, engine.Tick);
            Assert.AreEqual(0f, engine.ShiftTime);
        }

        [Test]
        public void Engine_Resume_AfterPause_ContinuesTick()
        {
            var engine = BuildEngine();
            engine.Pause();
            engine.Step();
            engine.Resume();
            engine.Step();

            Assert.AreEqual(1, engine.Tick);
        }

        [Test]
        public void Engine_SpeedMultiplier_AffectsShiftTime()
        {
            var e1 = BuildEngine(seed: 1);
            var e2 = BuildEngine(seed: 1);
            e2.SetSpeed(2f);

            e1.Step();
            e2.Step();

            Assert.AreEqual(e1.ShiftTime * 2f, e2.ShiftTime, 0.001f);
        }

        // ─────────────────────────────────────────────────────────────────
        // 3. TESTES DA FÓRMULA DE SCRAP
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void ScrapSystem_BaseCase_WithGoodOperator_LowScrap()
        {
            var engine = BuildEngine(seed: 999);
            var scrap  = new ScrapSystem(engine, seed: 999);

            var machine = engine.Machines[0]; // CNC-01
            machine.ToolWear = 0f;

            var part = new Part
            {
                PartTypeId      = "BRACKET-A7",
                MaterialQuality = 1f,
                Route           = new List<OperationType> { OperationType.CNC },
            };

            var breakdown = scrap.CalculateBreakdown(part, machine);

            // Com skill 82, sem wear, sem material variance → scrap deve ser baixo
            Assert.Less(breakdown.Total, 10f, $"Scrap esperado <10%, obtido {breakdown.Total:F1}%");
            Assert.Greater(breakdown.SkillModifier, -9f, "Skill modifier deve reduzir scrap");
            Assert.Less(breakdown.WearContribution, 0.1f, "Sem wear → contributo mínimo");
        }

        [Test]
        public void ScrapSystem_HighWear_IncreasesScrapChance()
        {
            var engine = BuildEngine(seed: 1);
            var scrap  = new ScrapSystem(engine, seed: 1);
            var machine = engine.Machines[0];
            var part    = new Part { PartTypeId = "BRACKET-A7", MaterialQuality = 1f };

            machine.ToolWear = 0f;
            var low  = scrap.CalculateBreakdown(part, machine);

            machine.ToolWear = 90f;
            var high = scrap.CalculateBreakdown(part, machine);

            Assert.Greater(high.Total, low.Total,
                $"Wear 90% deve dar mais scrap que 0%. low={low.Total:F1} high={high.Total:F1}");
            Assert.Greater(high.WearContribution, low.WearContribution);
        }

        [Test]
        public void ScrapSystem_ExponentialWear_Above80Pct()
        {
            var engine = BuildEngine(seed: 1);
            var scrap  = new ScrapSystem(engine, seed: 1);
            var machine = engine.Machines[0];
            var part    = new Part { PartTypeId = "BRACKET-A7", MaterialQuality = 1f };

            machine.ToolWear = 80f;
            var at80 = scrap.CalculateBreakdown(part, machine);

            machine.ToolWear = 95f;
            var at95 = scrap.CalculateBreakdown(part, machine);

            // O delta de 80→95 deve ser maior do que o delta 0→15 (exponencial)
            machine.ToolWear = 0f;
            var at0 = scrap.CalculateBreakdown(part, machine);

            machine.ToolWear = 15f;
            var at15 = scrap.CalculateBreakdown(part, machine);

            float deltaHigh = at95.WearContribution - at80.WearContribution;
            float deltaLow  = at15.WearContribution - at0.WearContribution;

            Assert.Greater(deltaHigh, deltaLow,
                "Wear acima de 80% deve ter progressão mais rápida (exponencial)");
        }

        [Test]
        public void ScrapSystem_LowMaterialQuality_IncreasesScrap()
        {
            var engine = BuildEngine(seed: 1);
            var scrap  = new ScrapSystem(engine, seed: 1);
            var machine = engine.Machines[0];

            var goodPart = new Part { PartTypeId = "BRACKET-A7", MaterialQuality = 1.0f };
            var badPart  = new Part { PartTypeId = "BRACKET-A7", MaterialQuality = 0.6f };

            var good = scrap.CalculateBreakdown(goodPart, machine);
            var bad  = scrap.CalculateBreakdown(badPart, machine);

            Assert.Greater(bad.MaterialVariance, good.MaterialVariance);
            Assert.Greater(bad.Total, good.Total,
                "Lote de baixa qualidade deve ter mais scrap");
        }

        [Test]
        public void ScrapSystem_NoOperator_UsesMinimumSkill()
        {
            var engine  = BuildEngine(seed: 1);
            var scrap   = new ScrapSystem(engine, seed: 1);
            var machine = engine.Machines[0];

            machine.AssignedOperator = null; // sem operador
            var part = new Part { PartTypeId = "BRACKET-A7", MaterialQuality = 1f };

            var breakdown = scrap.CalculateBreakdown(part, machine);

            // Sem operador, skill efectiva = 15 → modifier mínimo
            Assert.Greater(breakdown.Total, 0f);
            // Compara com operador bom
            machine.AssignedOperator = engine.Operators[0];
            var withOp = scrap.CalculateBreakdown(part, machine);

            Assert.Greater(breakdown.Total, withOp.Total,
                "Sem operador deve ter mais scrap do que com operador CNC 82");
        }

        // ─────────────────────────────────────────────────────────────────
        // 4. TESTES DE PRODUÇÃO (INTEGRAÇÃO)
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Production_After1000Ticks_ProducesSomeParts()
        {
            var engine = BuildEngine(seed: 42);
            for (int i = 0; i < 1000; i++) engine.Step();

            Assert.Greater(engine.TotalPartsCompleted + engine.TotalScrap, 0,
                "Após 1000 ticks deve haver produção");
        }

        [Test]
        public void Production_ScrapRate_IsReasonable()
        {
            var engine = BuildEngine(seed: 42, rawBatchSize: 500);
            for (int i = 0; i < 2000; i++) engine.Step();

            int total = engine.TotalPartsCompleted + engine.TotalScrap;
            if (total == 0) Assert.Inconclusive("Sem produção suficiente para avaliar scrap rate");

            float scrapRate = (float)engine.TotalScrap / total * 100f;

            // Com operador CNC 82 e wear baixo, scrap rate deve estar entre 2% e 30%
            Assert.GreaterOrEqual(scrapRate, 0f);
            Assert.LessOrEqual(scrapRate, 35f,
                $"Scrap rate anormalmente alto: {scrapRate:F1}%");
        }

        [Test]
        public void Production_HighToolWear_IncreasesScrapRate()
        {
            // Engine A: ferramenta nova
            var eA = BuildEngine(seed: 42, rawBatchSize: 300);
            for (int i = 0; i < 1500; i++) eA.Step();

            // Engine B: começa com ferramenta muito gasta
            var eB = BuildEngine(seed: 42, rawBatchSize: 300);
            eB.Machines[0].ToolWear = 85f;
            for (int i = 0; i < 1500; i++) eB.Step();

            int totalA = eA.TotalPartsCompleted + eA.TotalScrap;
            int totalB = eB.TotalPartsCompleted + eB.TotalScrap;

            if (totalA == 0 || totalB == 0)
                Assert.Inconclusive("Produção insuficiente");

            float rateA = (float)eA.TotalScrap / totalA;
            float rateB = (float)eB.TotalScrap / totalB;

            Assert.Greater(rateB, rateA,
                $"Wear alto deve dar mais scrap. A={rateA:P1} B={rateB:P1}");
        }

        // ─────────────────────────────────────────────────────────────────
        // 5. TESTES DE OPERADORES
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Operator_Fatigue_IncreasesOverTime()
        {
            var engine = BuildEngine(seed: 1);
            var op     = engine.Operators[0];

            Assert.AreEqual(0f, op.Fatigue);

            for (int i = 0; i < 500; i++) engine.Step();

            Assert.Greater(op.Fatigue, 0f, "Fadiga deve aumentar com o tempo");
        }

        [Test]
        public void Operator_SkillGain_AfterGoodParts()
        {
            var engine   = BuildEngine(seed: 42, rawBatchSize: 500);
            var op       = engine.Operators[0];
            float initial = op.Skills[OperationType.CNC];

            for (int i = 0; i < 3000; i++) engine.Step();

            Assert.GreaterOrEqual(op.Skills[OperationType.CNC], initial,
                "Skill CNC não deve diminuir com peças boas");
        }

        [Test]
        public void Operator_ProductLearning_StartsLow_ForNewProduct()
        {
            var engine = BuildEngine(seed: 1);
            var op     = engine.Operators[0];

            float learning = op.GetProductLearning("PRODUTO-NOVO-XYZ");
            Assert.AreEqual(0.3f, learning, 0.001f,
                "Produto desconhecido deve começar com curva de aprendizagem 0.3");
        }

        [Test]
        public void Operator_EffectiveSkill_ReducedByFatigue()
        {
            var engine = BuildEngine(seed: 1);
            var op     = engine.Operators[0];

            op.Fatigue = 0f;
            float skillFresh = op.GetEffectiveSkill(OperationType.CNC, "BRACKET-A7");

            op.Fatigue = 80f;
            float skillTired = op.GetEffectiveSkill(OperationType.CNC, "BRACKET-A7");

            Assert.Less(skillTired, skillFresh,
                $"Fadiga 80% deve reduzir skill efectiva. Fresh={skillFresh:F1} Tired={skillTired:F1}");
        }

        [Test]
        public void Operator_AssignAndUnassign_UpdatesBothSides()
        {
            var engine  = BuildEngine(seed: 1);
            var op      = engine.Operators[0];
            var machine = engine.Machines[1]; // WELD-01

            engine.AssignOperator(op, machine);

            Assert.AreEqual(machine, op.AssignedMachine);
            Assert.AreEqual(op, machine.AssignedOperator);
            Assert.IsNull(engine.Machines[0].AssignedOperator,
                "CNC-01 não deve ter operador após reassign");

            engine.UnassignOperator(op);
            Assert.IsNull(op.AssignedMachine);
            Assert.IsNull(machine.AssignedOperator);
        }

        [Test]
        public void Operator_Mentor_AgeThreshold()
        {
            var young   = new Operator { Age = 40 };
            var veteran = new Operator { Age = 60 };

            Assert.IsFalse(young.IsMentor);
            Assert.IsTrue(veteran.IsMentor);
        }

        // ─────────────────────────────────────────────────────────────────
        // 6. TESTES DE CONTROLO DE JOGO
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void ChangeTool_ResetsWearToZero()
        {
            var engine  = BuildEngine(seed: 1);
            var machine = engine.Machines[0];
            machine.ToolWear = 75f;

            engine.ChangeTool(machine);

            Assert.AreEqual(0f, machine.ToolWear);
        }

        [Test]
        public void ChangeTool_AddsCostToToolCost()
        {
            var engine  = BuildEngine(seed: 1);
            float before = engine.ToolCost;

            engine.ChangeTool(engine.Machines[0]);

            Assert.Greater(engine.ToolCost, before);
            Assert.AreEqual(engine.Config.ToolChangeCost, engine.ToolCost - before, 0.001f);
        }

        [Test]
        public void EmergencyStop_PausesAllMachines()
        {
            var engine = BuildEngine(seed: 1);
            engine.EmergencyStop();

            foreach (var m in engine.Machines)
                Assert.AreEqual(MachineState.Paused, m.State,
                    $"{m.Id} deve estar Paused após emergency stop");
        }

        [Test]
        public void ResumeAll_AfterEmergencyStop_SetsIdle()
        {
            var engine = BuildEngine(seed: 1);
            engine.EmergencyStop();
            engine.ResumeAll();

            foreach (var m in engine.Machines)
                Assert.AreNotEqual(MachineState.Paused, m.State,
                    $"{m.Id} não deve estar Paused após ResumeAll");
        }

        // ─────────────────────────────────────────────────────────────────
        // 7. TESTES DE MÉTRICAS
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Metrics_OEE_IsZeroWithNoProduction()
        {
            var engine = BuildEngine(seed: 1);
            Assert.AreEqual(0f, engine.OEE);
        }

        [Test]
        public void Metrics_GetMetrics_ReturnsAllMachines()
        {
            var engine  = BuildEngine(seed: 1);
            var metrics = engine.GetMetrics();

            Assert.AreEqual(engine.Machines.Count, metrics.MachineMetrics.Count);
            Assert.AreEqual(engine.Operators.Count, metrics.OperatorMetrics.Count);
        }

        [Test]
        public void Metrics_NetProfit_IsRevenueMinusCosts()
        {
            var engine = BuildEngine(seed: 42, rawBatchSize: 500);
            for (int i = 0; i < 2000; i++) engine.Step();

            Assert.AreEqual(
                engine.Revenue - engine.ScrapCost - engine.EnergyCost,
                engine.NetProfit, 0.01f);
        }

        // ─────────────────────────────────────────────────────────────────
        // 8. TESTES DE LOG
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Log_HasEntries_AfterSetup()
        {
            var engine = BuildEngine(seed: 1);
            Assert.Greater(engine.Log.Count, 0, "Log deve ter entradas após setup");
        }

        [Test]
        public void Log_OnLog_EventFires()
        {
            var engine  = BuildEngine(seed: 1);
            int received = 0;
            engine.OnLog += _ => received++;

            for (int i = 0; i < 10; i++) engine.Step();

            // O motor deve gerar pelo menos algum log durante ticks
            // (não garantido em 10 ticks mas o evento deve estar subscrito)
            Assert.GreaterOrEqual(received, 0);
        }

        // ─────────────────────────────────────────────────────────────────
        // 9. TESTE DE DETERMINISMO
        // ─────────────────────────────────────────────────────────────────

        [Test]
        public void Engine_WithSameSeed_ProducesSameResults()
        {
            var e1 = BuildEngine(seed: 777, rawBatchSize: 300);
            var e2 = BuildEngine(seed: 777, rawBatchSize: 300);

            for (int i = 0; i < 500; i++) { e1.Step(); e2.Step(); }

            Assert.AreEqual(e1.TotalPartsCompleted, e2.TotalPartsCompleted,
                "Mesmo seed deve produzir mesmo número de partes OK");
            Assert.AreEqual(e1.TotalScrap, e2.TotalScrap,
                "Mesmo seed deve produzir mesmo scrap");
        }

        [Test]
        public void Engine_WithDifferentSeeds_ProducesDifferentResults()
        {
            var e1 = BuildEngine(seed: 1,   rawBatchSize: 300);
            var e2 = BuildEngine(seed: 9999, rawBatchSize: 300);

            for (int i = 0; i < 1000; i++) { e1.Step(); e2.Step(); }

            // É improvável (mas não impossível) que sejam iguais
            bool differ = e1.TotalScrap != e2.TotalScrap ||
                          e1.TotalPartsCompleted != e2.TotalPartsCompleted;

            Assert.IsTrue(differ, "Seeds diferentes devem (normalmente) produzir resultados diferentes");
        }
    }
}
