// ============================================================
//  BubbleTemplateSelector.cs
// ============================================================
using System.Windows;
using System.Windows.Controls;

namespace ChatApp.ClientWpf
{
    public class BubbleTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Incoming { get; set; }
        public DataTemplate? Outgoing { get; set; }
        public DataTemplate? System   { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is BubbleVM b)
            {
                if (b.IsSystem) return System;
                return b.Own ? Outgoing : Incoming;
            }
            return base.SelectTemplate(item, container);
        }
    }
}
