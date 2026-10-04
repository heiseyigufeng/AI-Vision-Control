using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace MES_WPF.Views.ProductionManagement
{
    /// <summary>
    /// LabelInversionView.xaml 的交互逻辑
    /// </summary>
    public partial class LabelInversionView : UserControl
    {
        // 当前 Photo 图片路径
        private string _currentPhotoPath = "";
        // 当前 Detection 图片路径
        private string _currentResultPath = "";

        // 两个文件夹的完整路径（程序所在目录下）
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string OrigImageDir = Path.Combine(BaseDir, "OrigImage");
        private static readonly string ResultImageDir = Path.Combine(BaseDir, "ResultImage");

        public LabelInversionView()
        {
            InitializeComponent();

            // 进入界面时创建两个文件夹
            InitializeFolders();
        }

        /// <summary>
        /// 创建 OrigImage 和 ResultImage 两个文件夹（不存在时创建）
        /// </summary>
        private void InitializeFolders()
        {
            try
            {
                if (!Directory.Exists(OrigImageDir))
                    Directory.CreateDirectory(OrigImageDir);

                if (!Directory.Exists(ResultImageDir))
                    Directory.CreateDirectory(ResultImageDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create folders: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Load：打开文件对话框，选择图片并显示到 Photo 区域
        /// </summary>
        private void Btn_Load_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Image",
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;

                try
                {
                    // 加载图片
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    // 显示到 Photo 区域
                    PhotoImage.Source = bitmap;
                    PhotoImage.Visibility = Visibility.Visible;
                    PhotoPlaceholder.Visibility = Visibility.Collapsed;

                    // 保存当前路径
                    _currentPhotoPath = filePath;

                    // 更新路径文字
                    if (OrigPathText != null)
                    {
                        OrigPathText.Text = "Orig Path: " + filePath;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Load picture fail: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Photo 的 Open：打开 Photo 图片所在文件夹，并选中该文件
        /// </summary>
        private void Btn_Open_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderAndSelectFile(_currentPhotoPath);
        }

        /// <summary>
        /// Detection 的 Open：打开 Detection 图片所在文件夹，并选中该文件
        /// </summary>
        private void Btn_OpenResult_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderAndSelectFile(_currentResultPath);
        }

        /// <summary>
        /// 打开文件所在文件夹，并选中该文件
        /// </summary>
        private void OpenFolderAndSelectFile(string filePath)
        {
            // 检查路径是否为空
            if (string.IsNullOrEmpty(filePath))
            {
                MessageBox.Show("Images have not yet loaded.", "Message",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 检查文件是否存在
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File does not exist: {filePath}", "Message",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // /select, "文件路径" → 打开文件夹并选中该文件
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open the folder: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}