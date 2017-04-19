using Foundation;

namespace CustomUI
{
    public class MyObject : NSObject
    {
		[Export ("add:")]
		public int Add (int a, int b)
		{
			return a + b;
		}
    }
}
