using Microsoft.ML;
using SportMatchPredictor.ML.Data;

var mlContext = new MLContext(seed: 42);

Console.WriteLine("ML Trainer initialized.");
