using System.Collections.Generic;
using System.Text;

namespace DynamicNpcs
{
    /// <summary>
    /// Accumulates streamed LLM text deltas and emits complete sentences as they form,
    /// so TTS can start on the first sentence while the rest is still generating.
    /// </summary>
    public class SentenceChunker
    {
        private const string TrailingPunctuation = ".!?\"'”’)";

        private readonly StringBuilder _buffer = new StringBuilder();
        private readonly int _minChars;

        public SentenceChunker(int minChars = 24)
        {
            _minChars = minChars < 1 ? 1 : minChars;
        }

        /// <summary>Feed a streamed delta; returns any sentences completed by it.</summary>
        public List<string> Feed(string delta)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(delta))
                return results;

            _buffer.Append(delta);

            int start = 0;
            for (int i = 0; i < _buffer.Length; i++)
            {
                char c = _buffer[i];
                bool isBoundary = c == '.' || c == '!' || c == '?' || c == '\n';
                if (!isBoundary)
                    continue;

                // Swallow trailing punctuation/closing quotes ("...", ?!, .")
                int end = i + 1;
                while (end < _buffer.Length && TrailingPunctuation.IndexOf(_buffer[end]) >= 0)
                    end++;

                // If the boundary is at the very end of the buffer, more punctuation
                // may still be streaming in - wait, unless it was an explicit newline.
                if (end >= _buffer.Length && c != '\n')
                    break;

                if (end - start >= _minChars)
                {
                    string sentence = _buffer.ToString(start, end - start).Trim();
                    if (sentence.Length > 0)
                        results.Add(sentence);
                    start = end;
                }
                i = end - 1;
            }

            if (start > 0)
                _buffer.Remove(0, start);

            return results;
        }

        /// <summary>Returns whatever text remains after the stream ends.</summary>
        public string Flush()
        {
            string rest = _buffer.ToString().Trim();
            _buffer.Length = 0;
            return rest;
        }
    }
}
