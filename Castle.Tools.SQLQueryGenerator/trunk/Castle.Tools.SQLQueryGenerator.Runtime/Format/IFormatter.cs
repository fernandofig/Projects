namespace Castle.Tools.SQLQueryGenerator.Runtime.Format
{
	public interface IFormatter
	{
		string Format(Model.Field.IFormatableField field);
		string Format(Model.Table.IFormatableTable table);
	}
}