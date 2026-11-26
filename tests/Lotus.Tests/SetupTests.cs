extern alias setup;
using setup::Lotus.Setup;
using Xunit;

namespace Lotus.Tests
{
    public class PendingDeletesTests
    {
        private const string Folder = @"C:\Program Files\LOTUS";

        [Fact]
        public void Removes_the_folder_and_everything_inside_it()
        {
            string[] entries =
            {
                @"\??\C:\Windows\Temp\update.tmp", "",
                @"*1\??\C:\Program Files\LOTUS\Uninstall.exe", "",
                @"\??\C:\Program Files\Other\old.dll", @"!\??\C:\Program Files\Other\new.dll",
                @"*1\??\C:\Program Files\LOTUS", ""
            };

            string[] kept = PendingDeletes.Without(entries, Folder, out int removed);

            Assert.Equal(2, removed);
            Assert.Equal(new[]
            {
                @"\??\C:\Windows\Temp\update.tmp", "",
                @"\??\C:\Program Files\Other\old.dll", @"!\??\C:\Program Files\Other\new.dll"
            }, kept);
        }

        [Fact]
        public void Leaves_similar_looking_folders_alone()
        {
            string[] entries =
            {
                @"\??\C:\Program Files\LOTUS Notes\notes.exe", "",
                @"\??\C:\Program Files\LOTUS2\a.dll", "",
                @"\??\D:\Backup\C:\Program Files\LOTUS\Uninstall.exe", ""
            };

            string[] kept = PendingDeletes.Without(entries, Folder, out int removed);

            Assert.Equal(0, removed);
            Assert.Same(entries, kept);
        }

        [Fact]
        public void Matches_whatever_the_letter_case()
        {
            string[] entries = { @"\??\c:\program files\lotus\LOTUS.exe", "" };
            string[] kept = PendingDeletes.Without(entries, Folder, out int removed);
            Assert.Equal(1, removed);
            Assert.Empty(kept);
        }

        [Fact]
        public void A_list_that_is_not_made_of_pairs_is_left_untouched()
        {
            string[] entries = { @"\??\C:\Program Files\LOTUS\a", "", @"\??\C:\Program Files\LOTUS\b" };
            string[] kept = PendingDeletes.Without(entries, Folder, out int removed);
            Assert.Equal(0, removed);
            Assert.Same(entries, kept);
        }

        [Fact]
        public void An_empty_or_missing_list_is_fine()
        {
            Assert.Empty(PendingDeletes.Without(null, Folder, out int a));
            Assert.Empty(PendingDeletes.Without(new string[0], Folder, out int b));
            Assert.Equal(0, a + b);
        }
    }
}
