using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace UI.Recipe
{
    /// <summary>
    /// Interaction logic for RecipeEditorView.xaml
    /// </summary>
    public partial class RecipeEditorView : UserControl
    {
        public RecipeEditorView()
        {
            InitializeComponent();
        }

        private void LibraryItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            if (sender is FrameworkElement fe && fe.DataContext is StepLibraryItemViewModel step)
                DragDrop.DoDragDrop(fe, step, DragDropEffects.Copy);
        }

        private void StepTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is RecipeEditorViewModel vm)
                vm.SelectedNode = e.NewValue as RecipeStepNodeViewModel;
        }

        private void StepTreeView_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(StepLibraryItemViewModel)))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void StepTreeView_Drop(object sender, DragEventArgs e)
        {
            if (DataContext is not RecipeEditorViewModel vm)
                return;

            if (!e.Data.GetDataPresent(typeof(StepLibraryItemViewModel)))
                return;

            if (e.Data.GetData(typeof(StepLibraryItemViewModel)) is not StepLibraryItemViewModel libraryItem)
                return;

            var targetNode = FindTreeNodeUnderMouse(e.OriginalSource as DependencyObject);

            if (targetNode != null && targetNode.IsCycle)
                vm.AddLibraryStep(libraryItem.StepType, targetNode, InsertMode.Child);
            else
                vm.AddLibraryStep(libraryItem.StepType, targetNode, InsertMode.After);
        }

        private void NodeBorder_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe &&
                fe.DataContext is RecipeStepNodeViewModel node &&
                DataContext is RecipeEditorViewModel vm)
            {
                vm.SelectedNode = node;
                e.Handled = false;
            }
        }

        private static RecipeStepNodeViewModel? FindTreeNodeUnderMouse(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement fe && fe.DataContext is RecipeStepNodeViewModel node)
                    return node;

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }
    }
}
