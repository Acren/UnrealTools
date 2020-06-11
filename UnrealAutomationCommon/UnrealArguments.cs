using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon
{
    public class UnrealArguments : INotifyPropertyChanged
    {
        private bool useInsights = false;
        public bool UseInsights
        {
            get
            {
                return useInsights;
            }
            set
            {
                useInsights = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public override string ToString()
        {
            string Arguments = string.Empty;
            if(UseInsights)
            {
                Combine(ref Arguments, "-trace=cpu,frame,bookmark");
                Combine(ref Arguments, "-statnamedevents");
                Combine(ref Arguments, "-tracehost=127.0.0.1");
            }
            return Arguments;
        }

        public static void Combine(ref string Original, string NewArg)
        {
            if(!string.IsNullOrEmpty(Original))
            {
                Original += " ";
            }
            Original += NewArg;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
