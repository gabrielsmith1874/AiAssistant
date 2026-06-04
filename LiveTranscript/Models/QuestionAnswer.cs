using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LiveTranscript.Models
{
    /// <summary>
    /// A single extracted interview question with its answer.
    /// Supports INotifyPropertyChanged for expand/collapse binding.
    /// </summary>
    public class QuestionAnswer : INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        private string _paragraphAnswer = string.Empty;
        private string _keyPoints = string.Empty;
        private int _number;
        private int _followUpNumber;
        private bool _isFollowUp;
        private bool _isAnswerComplete;
        private string _parentQuestion = string.Empty;

        public int Number
        {
            get => _number;
            set
            {
                _number = value;
                OnPropertyChanged(nameof(Number));
                OnPropertyChanged(nameof(DisplayBadge));
            }
        }

        public int FollowUpNumber
        {
            get => _followUpNumber;
            set
            {
                _followUpNumber = value;
                OnPropertyChanged(nameof(FollowUpNumber));
                OnPropertyChanged(nameof(DisplayBadge));
            }
        }

        public bool IsFollowUp
        {
            get => _isFollowUp;
            set
            {
                _isFollowUp = value;
                OnPropertyChanged(nameof(IsFollowUp));
                OnPropertyChanged(nameof(DisplayBadge));
            }
        }

        public string ParentQuestion
        {
            get => _parentQuestion;
            set
            {
                _parentQuestion = value;
                OnPropertyChanged(nameof(ParentQuestion));
            }
        }

        public string Question { get; set; } = string.Empty;
        public ObservableCollection<QuestionAnswer> FollowUps { get; } = new();
        public string DisplayBadge => IsFollowUp ? $"F{FollowUpNumber}" : $"Q{Number}";

        public string ParagraphAnswer
        {
            get => _paragraphAnswer;
            set { _paragraphAnswer = value; OnPropertyChanged(nameof(ParagraphAnswer)); }
        }

        public string KeyPoints
        {
            get => _keyPoints;
            set { _keyPoints = value; OnPropertyChanged(nameof(KeyPoints)); }
        }

        public bool IsAnswerComplete
        {
            get => _isAnswerComplete;
            set { _isAnswerComplete = value; OnPropertyChanged(nameof(IsAnswerComplete)); }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
