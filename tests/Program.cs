using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OnScreenKeyboard;
class Check {
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
 static IEnumerable<Button> Buttons(System.Windows.DependencyObject root) {
  if(root is Button button) yield return button;
  for(int i=0; i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
   foreach(var child in Buttons(System.Windows.Media.VisualTreeHelper.GetChild(root,i))) yield return child;
 }

 [STAThread] static void Main(string[] args) {
  if (args.Length == 2 && args[0] == "--insertion-target") {
   var targetApp = new Application();
   var text = new TextBox();
   var target = new Window { Title="Vault insertion test", Width=320, Height=120, Content=text };
   var outputTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(200) };
   outputTimer.Tick+=(_,_)=> { outputTimer.Stop(); File.WriteAllText(args[1]+".text",text.Text); };
   text.TextChanged+=(_,_)=> { outputTimer.Stop(); outputTimer.Start(); };
   target.Loaded += (_, _) => {
    text.Focus();
    File.WriteAllText(args[1], new WindowInteropHelper(target).Handle.ToString());
   };
   targetApp.Run(target);
   return;
  }
  var app = new App(); app.InitializeComponent(); app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
  System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
  var vault = new Vault();
  vault.Items.Add(new VaultItem { Name="work-login", Value="private-value", Secret=true });
  vault.Items.Add(new VaultItem { Name="email", Value="hello@example.com" });
  var window = new VaultWindow(vault);
  T Find<T>(string name) => (T)window.FindName(name);
  void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
  void Invoke(string method, object sender) => typeof(VaultWindow).GetMethod(method, BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window, new object[]{sender, new RoutedEventArgs()});
  Assert(window.FindName("ItemList") == null && window.FindName("SearchBox") == null, "Editor contains only the form");
  window.EditItem(vault.Items[0]);
  Assert(Find<PasswordBox>("SecretValueBox").Visibility == Visibility.Visible, "Mask existing secret");
  Assert(Find<TextBox>("ValueBox").Text == "", "No hidden plaintext editor");
  var reveal=Find<CheckBox>("RevealBox"); reveal.IsChecked=true; Invoke("SecretDisplay_Changed",reveal);
  Assert(Find<TextBox>("ValueBox").Text == "private-value", "Reveal preserves value");
  reveal.IsChecked=false; Invoke("SecretDisplay_Changed",reveal);
  Assert(Find<PasswordBox>("SecretValueBox").Password == "private-value", "Mask preserves value");
  window.StartNew("sample");
  Assert(Find<TextBox>("NameBox").Text == "sample", "Prefilled new item");
  Assert(Find<TextBox>("ValueBox").Text == "sample", "Prefilled value");
  vault.Items[0].Favorite=true;
  Assert(vault.Search("")[0].Name == "work-login", "Favorites sort before alphabetical names");
  Assert(vault.Search("private-value").Count == 0, "Picker search excludes secret values");
  Assert(vault.Search("EXAMPLE").Single().Name == "email", "Picker searches non-secret values");
  var roundTrip = System.Text.Json.JsonSerializer.Deserialize<VaultItem>(System.Text.Json.JsonSerializer.Serialize(vault.Items[0]))!;
  Assert(roundTrip.Favorite, "Favorite survives serialization");
  Assert(!System.Text.Json.JsonSerializer.Deserialize<VaultItem>("{\"Name\":\"old-item\"}")!.Favorite, "Old entries remain compatible");
  var picker = new VaultPickerWindow(vault, 0);
  var pickerList=(ListBox)picker.FindName("ItemList");
  Assert(((VaultItem)pickerList.Items[0]).Favorite, "Picker displays favorites first");
  ((TextBox)picker.FindName("SearchBox")).Text="WORK";
  Assert(pickerList.Items.Count == 1, "Picker shortcut search");
  var insertMethod=typeof(VaultPickerWindow).GetMethod("Insert_Click",BindingFlags.Instance|BindingFlags.NonPublic)!;
  insertMethod.Invoke(picker,new object[]{new Button { DataContext=vault.Items[0] },new RoutedEventArgs()});
  Assert(((TextBlock)picker.FindName("Status")).Text.Contains("text field"), "Missing target blocks insertion");
  bool requested=false;
  picker.EditRequested += item => { requested=item == vault.Items[0]; };
  bool pickerClosed=false;
  picker.Closed+=(_,_)=>pickerClosed=true;
  typeof(VaultPickerWindow).GetMethod("Edit_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(picker,
   new object[]{new Button { DataContext=vault.Items[0] },new RoutedEventArgs()});
  Assert(requested && !pickerClosed, "Editing keeps the original list open");
  picker.Close();
  window.EditItem(vault.Items[0]);
  Assert(Find<TextBox>("NameBox").Text == "work-login", "Picker can select an entry in the editor");

  void PumpUntil(Func<bool> done, int timeout=5000) {
   var frame=new DispatcherFrame();
   var watch=Stopwatch.StartNew();
   var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(20) };
   timer.Tick += (_,_) => { if(done() || watch.ElapsedMilliseconds>timeout) frame.Continue=false; };
   timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
   Assert(done(), "Timed out waiting for insertion test");
  }
  // Interactive mode clicks a temporary picker and verifies text in a separate process.
  if (args.Contains("--interactive")) {
  var targetFile=Path.Combine(Path.GetTempPath(), "osk-insertion-"+Guid.NewGuid());
  using(var targetProcess=Process.Start(new ProcessStartInfo(Environment.ProcessPath!) {
   ArgumentList={"--insertion-target",targetFile}, UseShellExecute=false, CreateNoWindow=true
  })!) {
   try {
    PumpUntil(()=>File.Exists(targetFile));
    var targetHandle=nint.Parse(File.ReadAllText(targetFile));
    var insertionPicker=new VaultPickerWindow(vault,targetHandle) { Topmost=true };
    insertionPicker.Show(); insertionPicker.Activate();
    PumpUntil(()=>insertionPicker.IsActive);
    bool inserted=false; insertionPicker.Inserted+=()=>inserted=true;
    insertionPicker.UpdateLayout();
    var visibleList=(ListBox)insertionPicker.FindName("ItemList");
    var row=(ListBoxItem)visibleList.ItemContainerGenerator.ContainerFromIndex(0);
    Assert(row.ActualHeight <= 50, "Vault rows remain compact");
    Assert(Buttons(insertionPicker).Any(b=>b.Content as string == "Delete"), "Delete is available in the main list");
    var insert=Buttons(insertionPicker).Single(b=>b.Content as string == "Insert" && b.DataContext == vault.Items[1]);
    var point=insert.PointToScreen(new Point(insert.ActualWidth/2,insert.ActualHeight/2));
    Assert(SetCursorPos((int)point.X,(int)point.Y), "Could not position pointer for interactive test");
    mouse_event(2,0,0,0,0); mouse_event(4,0,0,0,0);
    PumpUntil(()=>inserted || !insertionPicker.IsEnabled);
    PumpUntil(()=>inserted || insertionPicker.IsEnabled);
    Assert(inserted, "Insertion failed: " + ((TextBlock)insertionPicker.FindName("Status")).Text);
    Assert(insertionPicker.IsVisible && insertionPicker.IsEnabled, "Vault stays open after insertion");
    PumpUntil(()=> {
     try { return File.Exists(targetFile+".text") && File.ReadAllText(targetFile+".text")==vault.Items[1].Value; }
     catch(IOException) { return false; }
    });
    Assert(File.ReadAllText(targetFile+".text")==vault.Items[1].Value, "Unexpected test text: " + File.ReadAllText(targetFile+".text"));
    insertionPicker.Close();
   } catch {
    Console.WriteLine("Received test text: " + (File.Exists(targetFile+".text") ? File.ReadAllText(targetFile+".text") : "<none>"));
    throw;
   } finally {
    targetProcess.Kill(); targetProcess.WaitForExit();
    File.Delete(targetFile); File.Delete(targetFile+".text");
   }
  }
  }
  if(args.Contains("--interactive")) Console.WriteLine("Passed: real-click insertion into a separate app.");
  var defaults = new Settings();
  Assert(defaults.ShowNavigationPanel, "Navigation panel enabled by default");
  var migrated = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"ShowNavRow\":false}")!;
  Assert(migrated.ShowNavigationPanel, "Old hidden navigation row must not hide the new panel");
  var saved = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"ShowNavigationPanel\":false}")!;
  Assert(!saved.ShowNavigationPanel, "Explicit panel preference is preserved");
  var keyboard = new MainWindow();
  Assert(((Grid)keyboard.FindName("NavigationKeys")).Children.Count == 2, "Permanent and optional navigation groups");
  var toggle=(ToggleButton)keyboard.FindName("NavToggle"); toggle.IsChecked=false;
  typeof(MainWindow).GetMethod("ApplyOptions",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(keyboard,null);
  var panel=(Grid)keyboard.FindName("NavigationKeys");
  Assert(((Grid)panel.Children[0]).Visibility == Visibility.Visible, "Delete and Escape always visible");
  Assert(((Grid)panel.Children[1]).Visibility == Visibility.Collapsed, "Extra navigation toggle");
  keyboard.Show();
  keyboard.BeginAnimation(Window.TopProperty,null);
  keyboard.BeginAnimation(Window.OpacityProperty,null);
  keyboard.UpdateLayout();
  var keyRows=(Grid)keyboard.FindName("KeyRows");
  var firstKey=Buttons(keyRows).First();
  var originalPosition=firstKey.PointToScreen(new Point(0,0));
  var originalSize=firstKey.RenderSize;
  var keyboardVault=(Vault)typeof(MainWindow).GetField("_vault",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(keyboard)!;
  var testSuggestion=new VaultItem { Name="floating-test-keyword", Value="dummy-test-value" };
  keyboardVault.Items.Add(testSuggestion);
  var wordField=typeof(MainWindow).GetField("_word",BindingFlags.Instance|BindingFlags.NonPublic)!;
  wordField.SetValue(keyboard,"floating-test");
  var showSuggestions=typeof(MainWindow).GetMethod("ShowSuggestions",BindingFlags.Instance|BindingFlags.NonPublic)!;
  showSuggestions.Invoke(keyboard,null);
  keyboard.UpdateLayout();
  Assert(((Popup)keyboard.FindName("SuggestionPopup")).IsOpen, "Suggestions float when a keyword matches");
  Assert(firstKey.PointToScreen(new Point(0,0))==originalPosition && firstKey.RenderSize==originalSize,
   "Showing suggestions never moves or resizes keys");
  wordField.SetValue(keyboard,""); showSuggestions.Invoke(keyboard,null);
  keyboardVault.Items.Remove(testSuggestion);
  keyboard.UpdateLayout();
  Assert(!((Popup)keyboard.FindName("SuggestionPopup")).IsOpen, "Suggestions dismiss when there are no matches");
  Assert(firstKey.PointToScreen(new Point(0,0))==originalPosition && firstKey.RenderSize==originalSize,
   "Hiding suggestions never moves or resizes keys");
  typeof(MainWindow).GetMethod("Vault_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(keyboard,
   new object[]{keyboard.FindName("VaultButton"),new RoutedEventArgs()});
  var openPicker=(VaultPickerWindow)typeof(MainWindow).GetField("_vaultPicker",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(keyboard)!;
  ((TextBox)openPicker.FindName("SearchBox")).Text="preserved search";
  typeof(VaultPickerWindow).GetMethod("Manage_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(openPicker,
   new object[]{new Button(),new RoutedEventArgs()});
  var editor=(VaultWindow)typeof(MainWindow).GetField("_vaultWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(keyboard)!;
  Assert(openPicker.IsVisible && editor.IsVisible && editor.Owner==openPicker, "Form opens on top of the original list");
  Assert(!openPicker.IsEnabled, "List actions wait until the form closes");
  openPicker.UpdateLayout();
  var disabledList=(ListBox)openPicker.FindName("ItemList");
  var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)disabledList.ActualWidth,
   (int)disabledList.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
  image.Render(disabledList);
  var pixel=new byte[4];
  image.CopyPixels(new Int32Rect(2,image.PixelHeight-2,1,1),pixel,4,0);
  Assert(pixel[0]<80 && pixel[1]<80 && pixel[2]<80 && pixel[3]==255,
   "Disabled vault list keeps its dark background while the editor is open");
  typeof(VaultWindow).GetMethod("Cancel_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,
   new object[]{new Button(),new RoutedEventArgs()});
  Assert(openPicker.IsVisible && openPicker.IsEnabled, "Cancel returns to the existing list");
  Assert(((TextBox)openPicker.FindName("SearchBox")).Text=="preserved search", "List search survives editing");
  openPicker.Close();
  Assert(KeyboardLayouts.NavRow().Any(k=>k.Vk==0x2C && !k.Repeat), "Print Screen definition");

  // Predictions: bundled lists, learning, next word, casing.
  var predictor=new Predictor();
  Assert(predictor.Complete("th",null).Contains("the"), "English completion from bundled list");
  Assert(predictor.Complete("Th",null)[0].StartsWith("Th"), "Completion follows typed casing");
  Assert(predictor.Complete("i'",null).Contains("I'm"), "Dictionary casing kept for I'm");
  Assert(predictor.Complete("مر",null).Count>0, "Arabic completion from bundled list");
  predictor.Learn(null,"thermostat");
  Assert(predictor.Complete("th",null)[0]=="thermostat", "A used word outranks unused list words");
  predictor.Learn(null,"zqxw");
  Assert(!predictor.Complete("zq",null).Contains("zqxw"), "One-off unknown words are not suggested");
  predictor.Learn(null,"zqxw");
  Assert(predictor.Complete("zq",null).Contains("zqxw"), "Repeated unknown words are suggested");
  predictor.Learn("good","morning"); predictor.Learn("good","morning"); predictor.Learn("good","night");
  Assert(predictor.PredictNext("Good")[0]=="morning", "Next-word prediction by frequency");
  Assert(predictor.Complete("n","good")[0]=="night", "Previous word boosts completions");
  predictor.Forget("zqxw"); Assert(predictor.Count("zqxw")==1, "Forget undoes one use");
  Assert(!predictor.IsKnown("zqxw"), "Not known yet");predictor.MarkKnown("zqxw"); Assert(predictor.IsKnown("zqxw"), "Mark known");
  var words=new WordsWindow(predictor);
  var wordList=(ListBox)words.FindName("WordList");
  Assert(wordList.Items.Cast<WordRow>().Any(r=>r.Word=="thermostat" && r.Removable), "Learned words listed");
  ((RadioButton)words.FindName("ArabicTab")).IsChecked=true;
  Assert(wordList.Items.Count==Predictor.BuiltInWords(true).Count, "Arabic list browsable");
  ((RadioButton)words.FindName("EnglishTab")).IsChecked=true;
  ((TextBox)words.FindName("SearchBox")).Text="THERMO";
  Assert(wordList.Items.Cast<WordRow>().All(r=>r.Word.Contains("thermo") && !r.Removable) && wordList.Items.Count>0, "English list search");
  ((RadioButton)words.FindName("MineTab")).IsChecked=true;
  typeof(WordsWindow).GetMethod("Remove_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(words,
   new object[]{new Button { DataContext=wordList.Items.Cast<WordRow>().First() },new RoutedEventArgs()});
  Assert(predictor.Count("thermostat")==0 && wordList.Items.Count==0, "Remove forgets a learned word");
  words.Close();
  var split=typeof(MainWindow).GetMethod("SplitWord",BindingFlags.Static|BindingFlags.NonPublic)!;
  var parts=((string,string,string))split.Invoke(null,new object[]{"(don't,"})!;
  Assert(parts==("(","don't",","), "Word split keeps punctuation around the word");
  Assert((((string,string,string))split.Invoke(null,new object[]{"ab1cd"})!).Item2=="", "Mixed tokens are not words");

  // Voice shortcut parsing round-trips.
  Assert(Shortcut.Parse("Win+H")!.ToString()=="Win+H", "Default voice shortcut");
  Assert(Shortcut.Parse("control + shift + space")!.ToString()=="Ctrl+Shift+Space", "Shortcut normalization");
  Assert(Shortcut.Parse("Ctrl+Win")!.Key==System.Windows.Input.Key.None, "Modifier-only shortcut");
  Assert(Shortcut.Parse("Alt+1")!.ToString()=="Alt+1" && Shortcut.Parse("F9")!=null, "Digits and function keys");
  var voiceDialog=new VoiceShortcutWindow(Shortcut.Parse("Ctrl+Win"));
  Assert(((TextBox)voiceDialog.FindName("ShortcutBox")).Text=="Ctrl+Win" && ((CheckBox)voiceDialog.FindName("WinBox")).IsChecked==true, "Voice dialog shows current shortcut");
  typeof(VoiceShortcutWindow).GetMethod("Reset_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(voiceDialog,new object[]{new Button(),new RoutedEventArgs()});
  Assert(((TextBox)voiceDialog.FindName("ShortcutBox")).Text=="Win+H", "Voice dialog resets to Windows voice typing");
  voiceDialog.Close();
  Assert(((Button)keyboard.FindName("VoiceShortcutButton")).Content.ToString()!.StartsWith("Shortcut: "), "Voice option label");
  Assert(Shortcut.Parse("Ctrl+H+J")==null && Shortcut.Parse("")==null && Shortcut.Parse("Ctrl+Bogus")==null, "Invalid shortcuts rejected");

  // Windows spell checker (skipped if no English spelling data is installed).
  var speller=new SpellChecker();
  if (speller.Languages.Any(l=>l.StartsWith("en"))) {
   Assert(speller.Check("hello")==null, "Correct word passes");
   var spell=speller.Check("helo");
   Assert(spell!=null && spell.Suggestions.Contains("hello"), "Misspelled word gets suggestions");
   Assert(speller.Check("teh")?.Replacement=="the", "Swapped letters are auto-correctable");
   Assert(speller.Check("Teh")?.Replacement==null, "Capitalized words are never auto-corrected");
   Assert(SpellChecker.TypoDistance("wierd","weird")==1 && SpellChecker.TypoDistance("cat","dog")==3, "Typo distance");
   Console.WriteLine("Spell checker languages: "+string.Join(", ",speller.Languages)+"; teh -> "+(speller.Check("teh")?.Replacement ?? "<none>"));
  }
  Console.WriteLine("Passed: WPF construction, search privacy, secret reveal/mask, new-item prefill, navigation visibility, Print Screen mapping, favorites, picker search, insertion guard, form ownership, and list preservation.");
  Environment.Exit(0);
 }
}
