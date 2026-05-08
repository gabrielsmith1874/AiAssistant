using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiveTranscript.Services
{
    internal static class AiPromptTemplates
    {
        public static string BuildQuestionExtractionSystemPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert interviewer.");
            sb.AppendLine("Extract interviewer questions from the transcript and detect follow-up questions.");
            sb.AppendLine("A follow-up question references a previous question context and would be unclear alone.");
            sb.AppendLine("OUTPUT FORMAT: Return ONLY a JSON array of objects. No preamble.");
            sb.AppendLine("[ { \"q\": \"question text\", \"f\": false, \"p\": \"\" }, { \"q\": \"follow-up text\", \"f\": true, \"p\": \"parent main question text\" } ]");
            sb.AppendLine("Rules:");
            sb.AppendLine("- Only include interviewer questions.");
            sb.AppendLine("- If a question is follow-up, set f=true and set p to the related main question.");
            sb.AppendLine("- If not follow-up, set f=false and p to empty string.");
            sb.AppendLine("- Keep question text natural and concise.");
            return sb.ToString();
        }

        public static string BuildQuestionExtractionUserPrompt(string transcript, IEnumerable<string>? knownQuestions = null)
        {
            var known = knownQuestions?.Where(q => !string.IsNullOrWhiteSpace(q)).ToList() ?? new List<string>();
            var sb = new StringBuilder();
            if (known.Count > 0)
            {
                sb.AppendLine("KNOWN PREVIOUS QUESTIONS:");
                foreach (var q in known)
                    sb.AppendLine($"- {q}");
                sb.AppendLine();
            }

            sb.AppendLine("TRANSCRIPT:");
            sb.AppendLine(transcript);
            return sb.ToString();
        }

        public static string BuildAnswerSystemPrompt(string jobDescription, string resume)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an experienced Software Engineer in an interview. Answer directly as the candidate.");
            sb.AppendLine();
            sb.AppendLine("CORE RULES:");
            sb.AppendLine("1. CONCISE & SPONTANEOUS: Keep the answer under 4 sentences. Speak naturally, as if thinking on your feet.");
            sb.AppendLine("2. STAR METHOD (IMPLICIT): Briefly touch on Situation/Task, focus on Action, and conclude with Result/Impact.");
            sb.AppendLine("3. NO FILLER: Avoid introductory fluff (e.g., 'That is a great question', 'I would approach this by'). Jump straight to the point.");
            sb.AppendLine("4. INTERVIEW SCORING: Demonstrate problem-solving, technical depth, ownership, and clear communication.");
            sb.AppendLine("5. TONE: Professional, confident, yet conversational. Sound like real speech (e.g., occasional 'so', 'then', or 'you know').");
            sb.AppendLine("6. NO FORMATTING: Return plain text only. No markdown, no bold, no bullet points, no lists.");
            sb.AppendLine("7. GROUNDING: Base your answer on the provided resume and job description. Do not invent unrelated experiences.");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(jobDescription))
            {
                sb.AppendLine("=== TARGET JOB DESCRIPTION ===");
                sb.AppendLine(jobDescription);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(resume))
            {
                sb.AppendLine("=== YOUR RESUME (TECHNICAL DATA) ===");
                sb.AppendLine(resume);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public static string BuildAnswerUserPrompt(
            string question,
            string transcript,
            string? parentQuestion = null,
            string? parentAnswer = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"QUESTION: {question}");
            if (!string.IsNullOrWhiteSpace(parentQuestion))
            {
                sb.AppendLine();
                sb.AppendLine($"PARENT QUESTION CONTEXT: {parentQuestion}");
                if (!string.IsNullOrWhiteSpace(parentAnswer))
                    sb.AppendLine($"PREVIOUS ANSWER CONTEXT: {parentAnswer}");
                sb.AppendLine("Treat this as a follow-up and keep continuity with the previous answer.");
            }

            sb.AppendLine();
            sb.AppendLine("CONTEXT TRANSCRIPT:");
            sb.AppendLine(transcript);
            return sb.ToString();
        }
    }
}
