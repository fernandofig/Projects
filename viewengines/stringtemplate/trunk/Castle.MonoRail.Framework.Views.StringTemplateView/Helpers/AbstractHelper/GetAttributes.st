$attrib.Keys,attrib.Values:{key,value |$if(key)$$if(value)$$key$="$value$"$else$$key$$endif$$endif$}; separator=" "$$\ $