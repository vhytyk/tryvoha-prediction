# Тривога prediction
This tool is using ML in order to predict air alarm in Ukraine region that could happen within 10 min interval. 
Data for training ML models (for each region) is based on posts from telegram channel https://t.me/air_alert_ua. Tool is listening for new posts in that channel and makes prediction based on current air alarm state in every region. Each hour ML models are being re-trained. 
