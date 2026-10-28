using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoSolid.Models;

namespace AutoSolid.Services
{
    public class AiExtractionManager
    {
        private readonly GeminiAiService _geminiService;
        private readonly OpenAiCompatibleService _openAiService;

        public AiExtractionManager()
        {
            _geminiService = new GeminiAiService();
            _openAiService = new OpenAiCompatibleService();
        }

        public GeminiAiService GeminiService => _geminiService;

        public async Task<List<ParametricRow>> ExtractDataAsync(
            string provider,
            string filePath,
            IList<DimensionMapping> mappings,
            string apiKey,
            string model,
            Action<double, string> onProgress = null,
            string userInstructions = null)
        {
            string lowerModel = (model ?? "").Trim().ToLowerInvariant();
            string lowerProvider = (provider ?? "").Trim().ToLowerInvariant();

            if (lowerModel.StartsWith("gemini") || (lowerProvider.Contains("gemini") && !lowerModel.StartsWith("gpt") && !lowerModel.StartsWith("deepseek")))
            {
                return await _geminiService.ExtractDataAsync(filePath, mappings, apiKey, model, onProgress, userInstructions);
            }
            else if (lowerModel.StartsWith("deepseek") || lowerProvider.Contains("deepseek"))
            {
                return await _openAiService.ExtractDataAsync(
                    filePath,
                    mappings,
                    apiKey,
                    model,
                    "https://api.deepseek.com/v1/chat/completions",
                    onProgress,
                    userInstructions
                );
            }
            else
            {
                return await _openAiService.ExtractDataAsync(
                    filePath,
                    mappings,
                    apiKey,
                    model,
                    "https://api.openai.com/v1/chat/completions",
                    onProgress,
                    userInstructions
                );
            }
        }
    }
}
