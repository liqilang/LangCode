using LangCode.Chat;
using LangCode.Domain.Chat;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LangCode.Ui.ToolWindows
{
    /// <summary>
    /// 历史记录抽屉（从右侧滑入，覆盖整个工具窗口）
    /// </summary>
    public partial class HistoryDrawerControl : UserControl
    {
        private ChatSession _session;
        private CollectionViewSource _view;
        private string _filter = string.Empty;

        public HistoryDrawerControl()
        {
            InitializeComponent();

            // 调色板（Ap.*）注入：跟随 VS 主题（深/浅），并支持运行期切换
            Theme.ThemeManager.Attach(this);
        }

        /// <summary>
        /// 是否处于多选模式：模板据此显示勾选框、隐藏单项删除、展开底部操作条
        /// </summary>
        public static readonly DependencyProperty IsMultiSelectProperty =
            DependencyProperty.Register(
                nameof(IsMultiSelect),
                typeof(bool),
                typeof(HistoryDrawerControl),
                new PropertyMetadata(false));

        public bool IsMultiSelect
        {
            get { return (bool)GetValue(IsMultiSelectProperty); }
            set { SetValue(IsMultiSelectProperty, value); }
        }

        /// <summary>
        /// 请求关闭（点返回按钮时抛出，由宿主收起抽屉）
        /// </summary>
        public event EventHandler CloseRequested;

        /// <summary>
        /// 用户选中了某个历史会话（参数是会话 id）
        /// </summary>
        public event EventHandler<string> ConversationSelected;

        /// <summary>
        /// 用户点了「新建会话」
        /// </summary>
        public event EventHandler NewConversationRequested;

        /// <summary>
        /// 删掉 / 清空之后抛出，宿主据此校正界面（例如正在看的会话被删了）
        /// </summary>
        public event EventHandler HistoryChanged;

        /// <summary>
        /// 接上会话：抽屉只负责**展示**（过滤 / 分组 / 高亮），
        /// 载入会话的动作交给宿主，因为它还要更新令牌条与滚动位置
        /// </summary>
        public void Attach(ChatSession session)
        {
            if (session == null || ReferenceEquals(session, _session))
            {
                return;
            }

            _session = session;

            _view = new CollectionViewSource { Source = session.History };

            // 「今天 / 昨天 / 更早」分组：设计稿里的分组标题由它提供，不再手写 TextBlock
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationSummary.GroupLabel)));
            _view.Filter += (_, e) => e.Accepted = Matches(e.Item as ConversationSummary);

            HistList.ItemsSource = _view.View;
        }

        public void Open()
        {
            // 每次打开都重新过滤一遍：期间可能已经新增/删除了会话
            Refresh();

            Visibility = Visibility.Visible;
            var distance = Math.Max(ActualWidth, 420);
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            Slide.X = distance;
            Slide.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        public void Close()
        {
            if (Visibility != Visibility.Visible)
            {
                return;
            }

            var animation = new DoubleAnimation(Math.Max(ActualWidth, 420), TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            animation.Completed += (_, __) => Visibility = Visibility.Collapsed;
            Slide.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        /// <summary>
        /// 把某条会话标记成「正在查看」（宿主载入会话后调用，保证列表与内容一致）
        /// </summary>
        public void SetActiveConversation(string id)
        {
            if (_session == null)
            {
                return;
            }

            foreach (var item in _session.History)
            {
                item.IsActive = string.Equals(item.Id, id, StringComparison.Ordinal);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void New_Click(object sender, RoutedEventArgs e) => NewConversationRequested?.Invoke(this, EventArgs.Empty);

        private async void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null || _session.History.Count == 0)
            {
                return;
            }

            // 清空是不可撤销的，先确认
            var confirmed = await Controls.InWindowDialog.ConfirmAsync(
                sender as FrameworkElement,
                "清空历史会话",
                "确定清空全部历史会话吗？该操作不可撤销",
                confirmText: "清空",
                iconName: "trash");

            if (!confirmed)
            {
                return;
            }

            _session.ClearHistory();
            Refresh();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            _filter = (HistSearch.Text ?? string.Empty).Trim();
            Refresh();
        }

        private void Item_Click(object sender, RoutedEventArgs e)
        {
            var summary = (sender as FrameworkElement)?.DataContext as ConversationSummary;
            if (summary == null)
            {
                return;
            }

            // 多选模式下：点击 = 勾选 / 取消勾选，不载入会话
            if (IsMultiSelect)
            {
                summary.IsSelected = !summary.IsSelected;
                UpdateMultiCount();
                return;
            }

            SetActiveConversation(summary.Id);
            ConversationSelected?.Invoke(this, summary.Id);
        }

        /// <summary>
        /// 进入 / 退出多选模式
        /// </summary>
        private void Multi_Click(object sender, RoutedEventArgs e)
        {
            IsMultiSelect = !IsMultiSelect;

            if (!IsMultiSelect)
            {
                ClearSelection();
            }

            UpdateMultiCount();

            // 资源引用（而非一次性取色）：主题切换时按钮颜色也会跟着变
            BtnMulti.SetResourceReference(Control.ForegroundProperty, IsMultiSelect ? "Ap.Accent" : "Ap.IconMuted");
        }

        private void CancelMulti_Click(object sender, RoutedEventArgs e)
        {
            IsMultiSelect = false;
            ClearSelection();
            BtnMulti.SetResourceReference(Control.ForegroundProperty, "Ap.IconMuted");
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
            {
                return;
            }

            var ids = _session.History.Where(x => x.IsSelected).Select(x => x.Id).ToList();
            if (ids.Count == 0)
            {
                IsMultiSelect = false;
                return;
            }

            var confirmed = await Controls.InWindowDialog.ConfirmAsync(
                sender as FrameworkElement,
                "删除选中的会话",
                "确定删除选中的 " + ids.Count + " 个会话吗？该操作不可撤销",
                confirmText: "删除",
                iconName: "trash");

            if (!confirmed)
            {
                return;
            }

            foreach (var id in ids)
            {
                _session.DeleteConversation(id);
            }

            IsMultiSelect = false;
            BtnMulti.SetResourceReference(Control.ForegroundProperty, "Ap.IconMuted");
            Refresh();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 清掉所有勾选
        /// </summary>
        private void ClearSelection()
        {
            if (_session == null)
            {
                return;
            }

            foreach (var item in _session.History)
            {
                item.IsSelected = false;
            }

            UpdateMultiCount();
        }

        /// <summary>
        /// 刷新底部「已选 N 项」
        /// </summary>
        private void UpdateMultiCount()
        {
            var count = _session == null ? 0 : _session.History.Count(x => x.IsSelected);
            MultiCount.Text = "已选 " + count + " 项";
        }

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            // 这枚按钮嵌在"整行可点"的载入按钮里，Click 会继续往上冒泡到那一行 ——
            // 不拦下来的话，不管点确定还是取消，行上的 Item_Click 都会把会话载入一次
            // （弹框只是把这件事延后到用户点完按钮之后，所以看起来像"点取消反而打开了会话"）
            e.Handled = true;

            // 删除按钮在条目模板内部，DataContext 就是那一行的 Summary
            var summary = (sender as FrameworkElement)?.DataContext as ConversationSummary;
            if (summary == null || _session == null)
            {
                return;
            }

            // 单条删除同样不可撤销：先把标题写出来让用户确认删的是哪一条
            // （多选 / 清空都有确认框，这里少一个就会"点一下会话就没了"）
            var title = string.IsNullOrWhiteSpace(summary.Title) ? "未命名会话" : summary.Title.Trim();
            if (title.Length > 40)
            {
                title = title.Substring(0, 40) + "…";
            }

            var confirmed = await Controls.InWindowDialog.ConfirmAsync(
                sender as FrameworkElement,
                "删除会话",
                "确定删除会话「" + title + "」吗？该操作不可撤销",
                confirmText: "删除",
                iconName: "trash");

            if (!confirmed)
            {
                return;
            }

            _session.DeleteConversation(summary.Id);
            Refresh();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 过滤 + 分组后刷新，并更新空状态文案
        /// </summary>
        private void Refresh()
        {
            _view?.View.Refresh();
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            var visible = _view == null ? 0 : _view.View.Cast<object>().Count();
            var hasAny = _session != null && _session.History.Count > 0;

            EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 区分「真的没有历史」与「只是被搜索词过滤掉了」
            EmptyText.Text = hasAny
                ? "没有匹配「" + _filter + "」的会话"
                : "暂无历史会话";
        }

        /// <summary>
        /// 标题 / 摘要 / 模型三处任一命中即可大小写不敏感，避免 ToLower 的文化差异坑
        /// </summary>
        private bool Matches(ConversationSummary item)
        {
            if (item == null)
            {
                return false;
            }

            if (_filter.Length == 0)
            {
                return true;
            }

            var haystack = item.Title + "\n" + item.Preview + "\n" + item.Model;
            return haystack.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

