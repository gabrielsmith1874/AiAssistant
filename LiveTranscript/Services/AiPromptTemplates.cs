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
            sb.AppendLine("8. CONTINUITY: Use prior answer history when provided. Build on what was already said and avoid repeating the same example or wording unless the interviewer asks for it.");
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
            string? parentAnswer = null,
            string? answerHistory = null)
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

            if (!string.IsNullOrWhiteSpace(answerHistory))
            {
                sb.AppendLine();
                sb.AppendLine("PRIOR ANSWER HISTORY:");
                sb.AppendLine(answerHistory);
                sb.AppendLine("Use this as interview continuity: build on prior answers and avoid repeating the same points unless the current question requires it.");
            }

            sb.AppendLine();
            sb.AppendLine("CONTEXT TRANSCRIPT:");
            sb.AppendLine(transcript);
            return sb.ToString();
        }

        public static string BuildJotNotesSystemPrompt(string jobDescription, string resume)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You create candidate-facing jot notes for live interview answers.");
            sb.AppendLine("Return plain text only. Use short dash-prefixed notes, not a script and not a full paragraph.");
            sb.AppendLine("Each note should name a must-say word, concept, tool, acronym, metric, or example, followed by a short description of what to say about it.");
            sb.AppendLine("If the question asks for a defined set of concepts, include each important term with a compact definition.");
            sb.AppendLine("Keep the notes glanceable but complete enough to craft a natural spoken answer.");
            sb.AppendLine("Avoid generic filler and do not repeat prior answers unless the current question depends on them.");
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

        public static string BuildJotNotesUserPrompt(
            string question,
            string paragraphAnswer,
            string transcript,
            string? parentQuestion = null,
            string? parentAnswer = null,
            string? answerHistory = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"QUESTION: {question}");

            if (!string.IsNullOrWhiteSpace(parentQuestion))
            {
                sb.AppendLine();
                sb.AppendLine($"PARENT QUESTION CONTEXT: {parentQuestion}");
                if (!string.IsNullOrWhiteSpace(parentAnswer))
                    sb.AppendLine($"PREVIOUS ANSWER CONTEXT: {parentAnswer}");
            }

            if (!string.IsNullOrWhiteSpace(answerHistory))
            {
                sb.AppendLine();
                sb.AppendLine("PRIOR ANSWER HISTORY:");
                sb.AppendLine(answerHistory);
            }

            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(paragraphAnswer))
            {
                sb.AppendLine("FULL PARAGRAPH ANSWER:");
                sb.AppendLine(paragraphAnswer);
                sb.AppendLine();
            }

            sb.AppendLine("CONTEXT TRANSCRIPT:");
            sb.AppendLine(transcript);
            sb.AppendLine();
            if (string.IsNullOrWhiteSpace(paragraphAnswer))
                sb.AppendLine("Create jot notes directly from the question and context.");
            else
                sb.AppendLine("Create the jot-note version now.");
            return sb.ToString();
        }
    }
}
