using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace KeyPulse.Helpers
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (Application.Current == null)
            {
                // If Application.Current is null, invoke directly
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                return;
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                // If on UI thread, invoke directly
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            } 
            else
            {
                // If not on UI thread, asynchronously invoke on UI thread
                Application.Current.Dispatcher.BeginInvoke(() => 
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName))
                );
            }
        }
    }
}
