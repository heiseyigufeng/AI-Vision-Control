using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MES_WPF.Services;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace MES_WPF.ViewModels
{
    public partial class LoginViewModel : ObservableObject
    {
        private readonly IAuthenticationService _authenticationService;

        [ObservableProperty]
        private string _username = "";

        [ObservableProperty]
        private string _password = "";

        [ObservableProperty]
        private string _errorMessage = "";

        [ObservableProperty]
        private bool _isLoading = false;

        /// <summary>
        /// 登录完成事件
        /// </summary>
        public event EventHandler<bool> LoginCompleted;

        /// <summary>
        /// 取消事件
        /// </summary>
        public event EventHandler CancelRequested;

        public LoginViewModel(IAuthenticationService authenticationService)
        {
            _authenticationService = authenticationService ?? throw new ArgumentNullException(nameof(authenticationService));
        }

        [RelayCommand]
        private async Task Login()
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "用户名和密码不能为空";
                return;
            }

            IsLoading = true;
            ErrorMessage = "";

            try
            {
                bool success = await _authenticationService.LoginAsync(Username, Password);

                if (success)
                {
                    LoginCompleted?.Invoke(this, true);
                }
                else
                {
                    ErrorMessage = "用户名或密码错误";
                    LoginCompleted?.Invoke(this, false);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"登录时发生错误: {ex.Message}";
                LoginCompleted?.Invoke(this, false);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// 取消登录
        /// </summary>
        [RelayCommand]
        private void Cancel()
        {
            // 触发取消事件（由 App.xaml.cs 监听并关闭窗口）
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}