using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiveTranscript.Services
{
    internal static class AiPromptTemplates
    {
        public static string BuildQuestionAnswerExtractionSystemPrompt(
            string jobDescription,
            string resume,
            bool useJotNotes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You extract interviewer questions from a live transcript and answer them as the candidate in one pass.");
            sb.AppendLine("A follow-up question references a previous question context and would be unclear alone.");
            sb.AppendLine("OUTPUT FORMAT: Return ONLY a JSON array of objects. No preamble, markdown, or code fence.");
            if (useJotNotes)
                sb.AppendLine("[ { \"q\": \"question text\", \"f\": false, \"p\": \"\", \"k\": \"- note: what to say\\n- term: why it matters\" } ]");
            else
                sb.AppendLine("[ { \"q\": \"question text\", \"f\": false, \"p\": \"\", \"a\": \"plain paragraph answer\" } ]");
            sb.AppendLine("Rules:");
            sb.AppendLine("- Only include new interviewer questions.");
            sb.AppendLine("- If no new interviewer question is present, return [].");
            sb.AppendLine("- If a question is follow-up, set f=true and set p to the related main question.");
            sb.AppendLine("- If not follow-up, set f=false and p to empty string.");
            sb.AppendLine("- Keep question text natural and concise.");
            sb.AppendLine("- Answer directly as the candidate. Do not say you are an AI.");
            sb.AppendLine("- Ground answers in the provided resume and job description. Do not invent unrelated experiences.");
            sb.AppendLine("- Use prior answer history for continuity and avoid repeating the same example unless the question requires it.");

            if (useJotNotes)
            {
                sb.AppendLine("- Generate jot notes only in k. Do not generate a paragraph answer.");
                sb.AppendLine("- Notes must be short dash-prefixed lines, glanceable but complete enough to craft a spoken answer.");
            }
            else
            {
                sb.AppendLine("- Generate a paragraph answer only in a. Do not generate jot notes or bullets.");
                sb.AppendLine("- Keep each answer under 4 sentences, conversational, specific, and interview-ready.");
            }

            if (!string.IsNullOrWhiteSpace(jobDescription))
            {
                sb.AppendLine();
                sb.AppendLine("=== TARGET JOB DESCRIPTION ===");
                sb.AppendLine(jobDescription);
            }

            if (!string.IsNullOrWhiteSpace(resume))
            {
                sb.AppendLine();
                sb.AppendLine("=== YOUR RESUME (TECHNICAL DATA) ===");
                sb.AppendLine(resume);
            }

            return sb.ToString();
        }

        public static string BuildQuestionAnswerExtractionUserPrompt(
            string transcript,
            IEnumerable<string>? knownQuestions = null,
            string? answerHistory = null)
        {
            var known = knownQuestions?.Where(q => !string.IsNullOrWhiteSpace(q)).ToList() ?? new List<string>();
            var sb = new StringBuilder();
            if (known.Count > 0)
            {
                sb.AppendLine("KNOWN PREVIOUS QUESTIONS:");
                sb.AppendLine("Skip exact repeats. Use these only as possible parent context for follow-up questions.");
                foreach (var q in known)
                    sb.AppendLine($"- {q}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(answerHistory))
            {
                sb.AppendLine("PRIOR ANSWER HISTORY:");
                sb.AppendLine(answerHistory);
                sb.AppendLine();
            }

            sb.AppendLine("TRANSCRIPT:");
            sb.AppendLine(transcript);
            return sb.ToString();
        }
    }
}
