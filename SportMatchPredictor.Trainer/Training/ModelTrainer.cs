using System;
using System.Linq;
using System.Globalization;
using Microsoft.ML;
using Microsoft.ML.Data;
using SportMatchPredictor.ML.Data;

namespace SportMatchPredictor.Trainer.Training;

public static class ModelTrainer
{
    public static void TrainAndEvaluate()
    {
        var root = Directory.GetCurrentDirectory();

        var dataPath = Path.Combine(root, "data", "processed", "dataset.csv");
        var modelDir = Path.Combine(root, "models");
        Directory.CreateDirectory(modelDir);
        var modelPath = Path.Combine(modelDir, "model.zip");

        if (!File.Exists(dataPath))
            throw new FileNotFoundException($"dataset.csv not found: {dataPath}");

        var ml = new MLContext(seed: 42);

        // 1) Load
        var data = ml.Data.LoadFromTextFile<MatchData>(
            path: dataPath,
            hasHeader: true,
            separatorChar: ','
        );

        // 2) Split
        // Time-based split: test = last season
        const string testSeason = "2015/2016";

        // Превращаем IDataView -> IEnumerable<MatchData>
        var allRows = ml.Data.CreateEnumerable<MatchData>(data, reuseRowObject: false);

        // Train: всё что строго меньше testSeason
        var trainRows = allRows.Where(r => string.CompareOrdinal(r.Season, testSeason) < 0);

        // Test: ровно testSeason
        var testRows = allRows.Where(r => string.Equals(r.Season, testSeason, StringComparison.Ordinal));

        // IEnumerable -> IDataView
        var trainData = ml.Data.LoadFromEnumerable(trainRows);
        var testData = ml.Data.LoadFromEnumerable(testRows);

        Console.WriteLine($"Train rows: {trainRows.Count()}");
        Console.WriteLine($"Test rows:  {testRows.Count()}");

        EvaluateBaselines(trainRows, testRows);

        // 3) Pipeline
        // Label (Result) -> key, Season -> one-hot, Features -> concatenate, normalize
        //var pipeline =
        //    ml.Transforms.Conversion.MapValueToKey("Label", nameof(MatchData.Result))
        //    .Append(ml.Transforms.Categorical.OneHotEncoding(nameof(MatchData.Season)))
        //    .Append(ml.Transforms.Concatenate("Features",
        //        nameof(MatchData.LeagueId),
        //        nameof(MatchData.Season),
        //        nameof(MatchData.HomeAvgGoalsFor),
        //        nameof(MatchData.HomeAvgGoalsAgainst),
        //        nameof(MatchData.HomePointsPerGame),
        //        nameof(MatchData.HomeWinRate),
        //        nameof(MatchData.AwayAvgGoalsFor),
        //        nameof(MatchData.AwayAvgGoalsAgainst),
        //        nameof(MatchData.AwayPointsPerGame),
        //        nameof(MatchData.AwayWinRate),
        //        nameof(MatchData.AvgGoalsForDiff),
        //        nameof(MatchData.AvgGoalsAgainstDiff),
        //        nameof(MatchData.PointsPerGameDiff),
        //        nameof(MatchData.WinRateDiff),
        //        nameof(MatchData.GoalDiffDiff)
        //    ))
        //    .Append(ml.Transforms.NormalizeMinMax("Features"))
        //    .Append(ml.MulticlassClassification.Trainers.OneVersusAll(
        //        ml.BinaryClassification.Trainers.FastTree(
        //            labelColumnName: "Label",
        //            featureColumnName: "Features"
        //        )))
        //    .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        //Console.WriteLine("Training...");
        //var model = pipeline.Fit(trainData);

        //Console.WriteLine("Evaluating...");
        //var predictions = model.Transform(testData);

        //var metrics = ml.MulticlassClassification.Evaluate(
        //    data: predictions,
        //    labelColumnName: "Label",
        //    predictedLabelColumnName: "PredictedLabel"
        //);

        //PrintMetrics(metrics);

        //// Save model
        //ml.Model.Save(model, trainData.Schema, modelPath);
        //Console.WriteLine($"Model saved: {modelPath}");

        //var engine = ml.Model.CreatePredictionEngine<MatchData, MatchPrediction>(model);

        // 1) Базовый пайплайн без тренера (одинаков для всех конфигов)
        var basePipeline =
            ml.Transforms.Conversion.MapValueToKey("Label", nameof(MatchData.Result))
            .Append(ml.Transforms.Categorical.OneHotEncoding(nameof(MatchData.Season)))
            .Append(ml.Transforms.Concatenate("Features",
                nameof(MatchData.LeagueId),
                nameof(MatchData.Season),
                nameof(MatchData.HomeAvgGoalsFor),
                nameof(MatchData.HomeAvgGoalsAgainst),
                nameof(MatchData.HomePointsPerGame),
                nameof(MatchData.HomeWinRate),
                nameof(MatchData.AwayAvgGoalsFor),
                nameof(MatchData.AwayAvgGoalsAgainst),
                nameof(MatchData.AwayPointsPerGame),
                nameof(MatchData.AwayWinRate),
                nameof(MatchData.AvgGoalsForDiff),
                nameof(MatchData.AvgGoalsAgainstDiff),
                nameof(MatchData.PointsPerGameDiff),
                nameof(MatchData.WinRateDiff),
                nameof(MatchData.GoalDiffDiff)
            ))
            .Append(ml.Transforms.NormalizeMinMax("Features"));

        // 2) Конфиги
        var configs = new[]
        {
            new FastTreeCfg("FT-1 (32/300/20)", 32, 300, 20),
            new FastTreeCfg("FT-2 (64/500/20)", 64, 500, 20),
            new FastTreeCfg("FT-3 (32/200/50)", 32, 200, 50),
        };

        ITransformer? bestModel = null;
        double bestMicro = double.NegativeInfinity;
        FastTreeCfg bestCfg = default;

        Console.WriteLine("\n==== Training configs (OVA + FastTree) ====");

        foreach (var cfg in configs)
        {
            var trainer = ml.MulticlassClassification.Trainers.OneVersusAll(
                ml.BinaryClassification.Trainers.FastTree(
                    labelColumnName: "Label",
                    featureColumnName: "Features",
                    numberOfLeaves: cfg.Leaves,
                    numberOfTrees: cfg.Trees,
                    minimumExampleCountPerLeaf: cfg.MinLeaf
                ));

            var pipeline = basePipeline
                .Append(trainer)
                .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

            Console.WriteLine($"\n--- {cfg.Name} ---");

            Console.WriteLine("Training...");
            var model = pipeline.Fit(trainData);

            Console.WriteLine("Evaluating...");
            var predictions = model.Transform(testData);

            var metrics = ml.MulticlassClassification.Evaluate(
                data: predictions,
                labelColumnName: "Label",
                predictedLabelColumnName: "PredictedLabel"
            );

            PrintMetrics(metrics);

            if (metrics.MicroAccuracy > bestMicro)
            {
                bestMicro = metrics.MicroAccuracy;
                bestModel = model;
                bestCfg = cfg;
            }
        }

        if (bestModel is null)
            throw new InvalidOperationException("No model was trained.");

        // Save best model
        ml.Model.Save(bestModel, trainData.Schema, modelPath);
        Console.WriteLine($"\n✅ Best: {bestCfg.Name} | MicroAccuracy={bestMicro:0.####}");
        Console.WriteLine($"Model saved: {modelPath}");

        // PredictionEngine должен быть от bestModel
        var engine = ml.Model.CreatePredictionEngine<MatchData, MatchPrediction>(bestModel);


        var sample = new MatchData
        {
            LeagueId = 1729,
            Season = "2015/2016",
            HomeAvgGoalsFor = 1.6f,
            HomeAvgGoalsAgainst = 1.0f,
            HomePointsPerGame = 1.8f,
            HomeWinRate = 0.55f,
            AwayAvgGoalsFor = 1.2f,
            AwayAvgGoalsAgainst = 1.4f,
            AwayPointsPerGame = 1.2f,
            AwayWinRate = 0.35f,

            AvgGoalsForDiff = 1.6f - 1.2f,
            AvgGoalsAgainstDiff = 1.0f - 1.4f,
            PointsPerGameDiff = 1.8f - 1.2f,
            WinRateDiff = 0.55f - 0.35f,
            GoalDiffDiff = (1.6f - 1.0f) - (1.2f - 1.4f),

            Result = 0
        };

        var pred = engine.Predict(sample);
        Console.WriteLine($"Sample predicted result: {pred.PredictedResult} (0=Away,1=Draw,2=Home)");
        if (pred.Score is { Length: > 0 })
            Console.WriteLine($"Scores: {string.Join(", ", pred.Score.Select(s => s.ToString("0.000", CultureInfo.InvariantCulture)))}");
    }

    private static void PrintMetrics(MulticlassClassificationMetrics m)
    {
        Console.WriteLine("==== Metrics ====");
        Console.WriteLine($"MicroAccuracy: {m.MicroAccuracy:0.####}");
        Console.WriteLine($"MacroAccuracy: {m.MacroAccuracy:0.####}");
        Console.WriteLine($"LogLoss:       {m.LogLoss:0.####}");
        Console.WriteLine($"LogLossRed.:   {m.LogLossReduction:0.####}");

        // Confusion matrix
        Console.WriteLine("\nConfusion Matrix:");
        var cm = m.ConfusionMatrix;
        for (int i = 0; i < cm.NumberOfClasses; i++)
        {
            Console.WriteLine(string.Join(" ", cm.Counts[i].Select(v => v.ToString().PadLeft(6))));
        }
    }

    private readonly record struct FastTreeCfg(string Name, int Leaves, int Trees, int MinLeaf);

    private static void EvaluateBaselines(IEnumerable<MatchData> trainRows, IEnumerable<MatchData> testRows)
    {
        // Важно: материализуем, чтобы не перечислять несколько раз
        var train = trainRows.ToList();
        var test = testRows.ToList();

        // Baseline 1: всегда Home win (2)
        var alwaysHomeAcc = test.Count == 0
            ? 0
            : test.Count(r => (int)r.Result == 2) / (double)test.Count;

        // Baseline 2: самый частый класс в train
        int mostFrequent = train
            .GroupBy(r => (int)r.Result)
            .OrderByDescending(g => g.Count())
            .First().Key;

        var mostFreqAcc = test.Count == 0
            ? 0
            : test.Count(r => (int)r.Result == mostFrequent) / (double)test.Count;

        Console.WriteLine("\n==== Baselines ====");
        Console.WriteLine($"Always Home (2) accuracy: {alwaysHomeAcc:0.####}");
        Console.WriteLine($"Most frequent class in train ({mostFrequent}) accuracy: {mostFreqAcc:0.####}");
    }
}
